using System.Text;
using System.Text.Json.Nodes;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Puluj.Transport.Spike.Tests.Spike;

/// <summary>Що робить handler з доставкою (для crash tests).</summary>
public enum Behaviour
{
    /// <summary>Робота → commit (inbox + effect + receipt) → ACK.</summary>
    Normal,
    /// <summary>W4: «падіння» до commit — нічого не записано, ACK не надіслано, канал закривається.</summary>
    CrashBeforeCommit,
    /// <summary>W5: commit виконано, «падіння» до ACK — канал закривається без ACK.</summary>
    CrashAfterCommitBeforeAck,
    /// <summary>W6: бізнес-обробка завжди падає → attempt у «БД»; до MaxAttempts — nack(requeue) (негайний retry; backoff — P03),
    /// на MaxAttempts — receipt quarantined (commit) → nack(requeue=false) → DLX → DLQ.</summary>
    AlwaysFail,
}

/// <summary>
/// Consumer з manual ACK після «commit» і inbox dedup (ADR-0004 §6.2). Одна репліка = один канал/consumer tag.
/// </summary>
public sealed class InboxConsumer : IAsyncDisposable
{
    private readonly FileStore _store;
    private readonly string _subscription;
    private readonly string _replica;
    private IChannel? _channel;
    private readonly TaskCompletionSource _crashed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Delivered { get; private set; }
    public int RedeliveredSeen { get; private set; }
    /// <summary>Максимальний x-delivery-count (quorum queues) серед доставок — evidence для delivery-limit.</summary>
    public long MaxDeliveryCount { get; private set; }
    public string LastHeaderType { get; private set; } = "";
    public List<string> EventIds { get; } = [];
    public Behaviour Behaviour { get; set; } = Behaviour.Normal;
    /// <summary>Ліміт бізнес-спроб (queue_policies.max_delivery_attempts); лічильник — у FileStore, не в брокері.</summary>
    public int MaxAttempts { get; set; } = 5;
    public TimeSpan WorkDelay { get; set; } = TimeSpan.Zero;
    /// <summary>Спрацьовує, коли handler «впав» (канал закрито без ACK).</summary>
    public Task Crashed => _crashed.Task;

    public InboxConsumer(FileStore store, string subscription, string replica = "1")
    {
        _store = store;
        _subscription = subscription;
        _replica = replica;
    }

    public async Task StartAsync(IConnection connection, string queue, ushort prefetch = 10, CancellationToken ct = default)
    {
        _channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await _channel.BasicQosAsync(0, prefetch, false, ct);
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnReceivedAsync;
        await _channel.BasicConsumeAsync(queue, autoAck: false, consumerTag: $"{_subscription}-{_replica}", consumer, cancellationToken: ct);
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        var channel = _channel!;
        var eventId = ea.BasicProperties.MessageId ?? JsonNode.Parse(Encoding.UTF8.GetString(ea.Body.Span))!["event_id"]!.GetValue<string>();
        var key = FileStore.InboxKey(_subscription, eventId);
        lock (EventIds)
        {
            Delivered++;
            if (ea.Redelivered)
            {
                RedeliveredSeen++;
            }
            if (ea.BasicProperties.Headers is { } headers && headers.TryGetValue("x-delivery-count", out var count) && count is not null)
            {
                MaxDeliveryCount = Math.Max(MaxDeliveryCount, Convert.ToInt64(count));
                LastHeaderType = count.GetType().Name;
            }
            EventIds.Add(eventId);
        }

        // 1. Idempotency: inbox hit → ACK без бізнес-ефекту (W3/W5/W7b/W14a).
        if (_store.Read(s => s.Inbox.ContainsKey(key)))
        {
            _store.Commit(s => s.DuplicatesSuppressed++);
            await channel.BasicAckAsync(ea.DeliveryTag, false);
            Interlocked.Increment(ref _ackedBacking);
            return;
        }

        switch (Behaviour)
        {
            case Behaviour.CrashBeforeCommit:
                Behaviour = Behaviour.Normal; // наступна репліка/redelivery обробляє нормально
                Crash();
                return;
            case Behaviour.AlwaysFail:
            {
                var attempts = _store.Commit(s => s.Attempts[key] = s.Attempts.GetValueOrDefault(key) + 1);
                if (attempts < MaxAttempts)
                {
                    await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: true);
                    return;
                }
                // W6a-2: durable quarantine receipt ДО transfer у DLQ; повторний transfer ідемпотентний.
                _store.Commit(s => s.Receipts[key] = "quarantined");
                await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: false);
                return;
            }
        }

        // 2. Робота поза «транзакцією».
        if (WorkDelay > TimeSpan.Zero)
        {
            await Task.Delay(WorkDelay);
        }

        // 3. Коротка «транзакція»: re-check inbox, ефект, inbox completion, receipt — атомарно у FileStore.
        var applied = _store.Commit(s =>
        {
            if (s.Inbox.ContainsKey(key))
            {
                s.DuplicatesSuppressed++;
                return false;
            }
            s.Inbox[key] = DateTimeOffset.UtcNow;
            s.Effects[key] = s.Effects.GetValueOrDefault(key) + 1;
            s.Receipts[key] = "completed";
            if (ea.Redelivered)
            {
                s.Redelivered++;
            }
            return true;
        });

        if (Behaviour == Behaviour.CrashAfterCommitBeforeAck)
        {
            Behaviour = Behaviour.Normal;
            Crash();
            return;
        }

        // 4. Commit → ACK.
        _ = applied;
        await channel.BasicAckAsync(ea.DeliveryTag, false);
        Interlocked.Increment(ref _ackedBacking);
    }

    private int _ackedBacking;
    public int AckedCount => Volatile.Read(ref _ackedBacking);

    /// <summary>«Падіння» процесу: канал закривається без ACK з іншого потоку (закриття зсередини consumer dispatch loop
    /// чекало б на завершення цього ж callback) → unacked повідомлення повертаються в чергу з redelivered=true.</summary>
    private void Crash()
    {
        var channel = _channel;
        _channel = null;
        _ = Task.Run(async () =>
        {
            try
            {
                if (channel is not null)
                {
                    await channel.CloseAsync();
                    await channel.DisposeAsync();
                }
            }
            finally
            {
                _crashed.TrySetResult();
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        var channel = _channel;
        _channel = null;
        if (channel is not null && channel.IsOpen)
        {
            await channel.CloseAsync();
        }
        if (channel is not null)
        {
            await channel.DisposeAsync();
        }
    }
}
