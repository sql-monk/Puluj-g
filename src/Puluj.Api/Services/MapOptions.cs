namespace Puluj.Api.Services;

/// <summary>
/// The time windows of the live map (`Map` section). One source of truth for server and client: the live snapshot
/// only carries what a marker lifetime can still show, the feed only its window, the realtime bridge only pushes
/// what falls inside them, and the client gets the same numbers from /api/map/config for its lifetime picker and
/// its own pruning. Invalid values fall back to the defaults: a misconfigured section must not empty the map.
/// </summary>
public sealed class MapOptions
{
    public const string Section = "Map";

    private static readonly int[] DefaultLifetimeOptions = [5, 10, 15, 20, 30, 45, 60, 120];

    private int[] _lifetimeOptionsMinutes = DefaultLifetimeOptions;
    private double _feedHours = 6;
    private double _snapshotCacheSeconds = 5;

    /// <summary>Choices of "how long a marker stays after its last message" offered on the panel, minutes.</summary>
    public int[] LifetimeOptionsMinutes
    {
        get => _lifetimeOptionsMinutes;
        set => _lifetimeOptionsMinutes = value is { Length: > 0 } && value.All(m => m > 0) ? value.Distinct().Order().ToArray() : DefaultLifetimeOptions;
    }

    /// <summary>The longest lifetime a viewer can pick: the window of the live snapshot and of track pushes.</summary>
    public TimeSpan MaxLifetime => TimeSpan.FromMinutes(LifetimeOptionsMinutes[^1]);

    /// <summary>How far back the feed of recent targets goes in live mode, hours.</summary>
    public double FeedHours
    {
        get => _feedHours;
        set => _feedHours = value > 0 ? value : 6;
    }

    public TimeSpan FeedWindow => TimeSpan.FromHours(FeedHours);

    /// <summary>How long one live snapshot is shared by every client asking for it; 0 disables the cache.</summary>
    public double SnapshotCacheSeconds
    {
        get => _snapshotCacheSeconds;
        set => _snapshotCacheSeconds = value >= 0 ? value : 5;
    }

    public TimeSpan SnapshotCache => TimeSpan.FromSeconds(SnapshotCacheSeconds);

}
