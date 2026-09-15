using System.Text.Json;

namespace Puluj.Transport.Spike.Tests.Spike;

/// <summary>
/// Заглушка durable outbox/inbox/receipts для spike: JSON-файл на диску, запис на кожну зміну. Імітує «commit» БД,
/// щоб crash tests мали durable стан між «падінням» і повторною доставкою. P03 замінює на messaging.outbox/inbox.
/// </summary>
public sealed class FileStore
{
    public sealed class OutboxRow
    {
        public required string EventId { get; init; }
        public required string RoutingKey { get; init; }
        public required string Body { get; init; }
        public int Attempts { get; set; }
        public DateTimeOffset? ConfirmedAt { get; set; }
        public bool Returned { get; set; }
    }

    public sealed class State
    {
        public Dictionary<string, OutboxRow> Outbox { get; set; } = new(StringComparer.Ordinal);
        /// <summary>subscription:event_id → completed_at (inbox completion, записується в тій самій «транзакції», що й ефект).</summary>
        public Dictionary<string, DateTimeOffset> Inbox { get; set; } = new(StringComparer.Ordinal);
        /// <summary>subscription:event_id → completed | noop | quarantined.</summary>
        public Dictionary<string, string> Receipts { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Бізнес-ефект: скільки разів результат застосовано (має бути 1 на event на підписку).</summary>
        public Dictionary<string, int> Effects { get; set; } = new(StringComparer.Ordinal);
        /// <summary>subscription:event_id → кількість бізнес-спроб (processing.attempts у P03).</summary>
        public Dictionary<string, int> Attempts { get; set; } = new(StringComparer.Ordinal);
        public int DuplicatesSuppressed { get; set; }
        public int Redelivered { get; set; }
    }

    private readonly string _path;
    private readonly bool _persist;
    private readonly object _gate = new();
    private State _state = new();

    /// <param name="persist">false — лише in-memory (smoke load: запис файлу на кожен commit O(n²) і не є предметом виміру).</param>
    public FileStore(string path, bool persist = true)
    {
        _path = path;
        _persist = persist;
        if (File.Exists(path))
        {
            _state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State();
        }
    }

    public static FileStore Temp(string name, bool persist = true) => new(Path.Combine(Path.GetTempPath(), "puluj-spike", $"{name}-{Guid.NewGuid():N}.json"), persist);
    public string FilePath => _path;

    public T Read<T>(Func<State, T> reader)
    {
        lock (_gate)
        {
            return reader(_state);
        }
    }

    /// <summary>Одна «транзакція»: зміна + запис файлу. Winner = перший, хто закомітив.</summary>
    public T Commit<T>(Func<State, T> mutation)
    {
        lock (_gate)
        {
            var result = mutation(_state);
            if (_persist)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_state));
            }
            return result;
        }
    }

    public void Commit(Action<State> mutation) => Commit(s => { mutation(s); return 0; });

    public static string InboxKey(string subscription, string eventId) => $"{subscription}:{eventId}";
}
