using System.Text;
using System.Text.Json.Nodes;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Puluj.Transport.Spike.Tests.Spike;

/// <summary>
/// Outbox → relay → publisher confirms (ADR-0004 W2/W3/W7): рядок outbox лишається unconfirmed до confirm брокера;
/// повторна публікація несе той самий event_id (стабільний), тож дублікат поглинає inbox consumer.
/// </summary>
public sealed class OutboxPublisher(FileStore store, Topology topology)
{
    private readonly JsonObject _template = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "messaging", "fixtures", "valid", "raw.stored.json")))!.AsObject();

    /// <summary>Envelope з fixture raw.stored з новими event_id/lane/raw_message_id (payload — той самий).</summary>
    public string Enqueue(string lane, string? eventId = null, long rawMessageId = 4711, string eventType = "raw.stored", int paddingBytes = 0)
    {
        var envelope = _template.DeepClone().AsObject();
        eventId ??= Guid.CreateVersion7().ToString();
        envelope["event_id"] = eventId;
        envelope["event_type"] = eventType;
        envelope["lane"] = lane;
        envelope["raw_message_id"] = rawMessageId;
        envelope["payload"]!["raw_message_id"] = rawMessageId;
        if (paddingBytes > 0)
        {
            envelope["x_padding"] = new string('x', paddingBytes);
        }
        var body = envelope.ToJsonString();
        store.Commit(s => s.Outbox[eventId] = new FileStore.OutboxRow { EventId = eventId, RoutingKey = topology.RoutingKey(lane, eventType), Body = body });
        return eventId;
    }

    /// <summary>
    /// Relay: публікує всі unconfirmed рядки (persistent, mandatory), чекає confirm кожного не довше confirmTimeout,
    /// позначає confirmed_at. Повертає кількість підтверджених; unconfirmed лишаються для наступного проходу.
    /// Повернуті (unroutable) повідомлення позначаються Returned і не вважаються доставленими.
    /// </summary>
    public async Task<int> RelayAsync(IConnection connection, TimeSpan confirmTimeout, CancellationToken ct = default, bool markConfirmed = true)
    {
        var pending = store.Read(s => s.Outbox.Values.Where(r => r.ConfirmedAt is null).Select(r => (r.EventId, r.RoutingKey, r.Body)).ToList());
        if (pending.Count == 0)
        {
            return 0;
        }
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        var returned = new HashSet<string>(StringComparer.Ordinal);
        channel.BasicReturnAsync += (_, e) =>
        {
            if (e.BasicProperties.MessageId is { } id)
            {
                lock (returned)
                {
                    returned.Add(id);
                }
            }
            return Task.CompletedTask;
        };

        var confirmed = 0;
        foreach (var (eventId, routingKey, body) in pending)
        {
            store.Commit(s => s.Outbox[eventId].Attempts++);
            var props = new BasicProperties { Persistent = true, MessageId = eventId, ContentType = "application/json", Type = routingKey };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(confirmTimeout);
            try
            {
                await channel.BasicPublishAsync(topology.ExchangeName, routingKey, mandatory: true, basicProperties: props, body: Encoding.UTF8.GetBytes(body), cancellationToken: timeout.Token);
            }
            catch (PublishException)
            {
                // Spike спрощує: nack брокера і basic.return (unroutable) обидва позначаються Returned.
                // TODO P03: розрізняти PublishException.IsReturn / PublishReturnException (unroutable → alarm «missing
                // binding», без retry) і nack (→ retry з backoff); зараз рядок лишається в outbox без confirm в обох випадках.
                store.Commit(s => s.Outbox[eventId].Returned = true);
                continue;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // delayed/lost confirm (W7b): рядок лишається unconfirmed → наступний relay опублікує повторно з тим самим event_id
                continue;
            }
            bool wasReturned;
            lock (returned)
            {
                wasReturned = returned.Contains(eventId);
            }
            if (wasReturned)
            {
                store.Commit(s => s.Outbox[eventId].Returned = true);
                continue;
            }
            if (markConfirmed)
            {
                store.Commit(s => s.Outbox[eventId].ConfirmedAt = DateTimeOffset.UtcNow);
            }
            confirmed++;
        }
        return confirmed;
    }
}
