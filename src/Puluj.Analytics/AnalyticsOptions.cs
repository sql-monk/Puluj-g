namespace Puluj.Analytics;

/// <summary>Settings of the analytics service (`Analytics` section).</summary>
public sealed class AnalyticsOptions
{
    public const string Section = "Analytics";

    /// <summary>Instance name for the heartbeat (`Runtime:Worker:{Name}:Heartbeat`), logs and the runs table.</summary>
    public string Name { get; set; } = "analytics";

    /// <summary>Sleep between runs once the backlog is drained.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Raw messages taken per transaction.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Only rows received at least this long ago are taken, so the watermark never passes a row whose insert is still uncommitted.</summary>
    public TimeSpan SafetyLag { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Days of `track_firsts` rebuilt after each run.</summary>
    public int TrackFirstsDays { get; set; } = 30;

}
