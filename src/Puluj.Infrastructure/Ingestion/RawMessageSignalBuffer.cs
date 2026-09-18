using System.Threading.Channels;

namespace Puluj.Infrastructure.Ingestion;

/// <summary>
/// In-process wake-up hints for newly stored raw messages. Durable pending state and claiming live in PostgreSQL;
/// losing or duplicating a hint is harmless because the processor polls Pending rows.
/// </summary>
public interface IRawMessageSignalBuffer
{
    ValueTask AnnounceAsync(long rawMessageId, CancellationToken ct = default);
    bool TryTake(out long rawMessageId);
    ValueTask<bool> WaitAsync(CancellationToken ct);
}

public sealed class RawMessageSignalBuffer : IRawMessageSignalBuffer
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    public ValueTask AnnounceAsync(long rawMessageId, CancellationToken ct = default) => _channel.Writer.WriteAsync(rawMessageId, ct);
    public bool TryTake(out long rawMessageId) => _channel.Reader.TryRead(out rawMessageId);
    public ValueTask<bool> WaitAsync(CancellationToken ct) => _channel.Reader.WaitToReadAsync(ct);
}
