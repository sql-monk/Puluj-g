namespace Puluj.Processing.Correlation;

public sealed class CorrelationOptions
{
    public const string Section = "Correlation";
    /// <summary>Global database lookup window for possible tracks; pair scoring still uses the class profile window.</summary>
    public int CandidateWindowMinutes { get; set; } = 120;
    /// <summary>Targets of the same thing from different messages within this window are duplicates.</summary>
    public TimeSpan DuplicateWindow { get; set; } = TimeSpan.FromMinutes(3);
    /// <summary>Minimum association score to attach an target to an existing track.</summary>
    public double AttachThreshold { get; set; } = 0.6;
    /// <summary>Minimum lead over the second-best candidate required to attach to an existing track.</summary>
    public double AmbiguityMargin { get; set; } = 0.05;
    /// <summary>Extra distance tolerance on top of the class speed × time and reported location areas.</summary>
    public double SlackKm { get; set; } = 8;
    /// <summary>Two reports this imprecise cannot identify one moving object without a more specific fact.</summary>
    public double CoarseLocationAccuracyKm { get; set; } = 80;
    /// <summary>Tracks without updates for this many correlation windows are closed by the watchdog.</summary>
    public double CloseAfterWindows { get; set; } = 2;
    public TimeSpan WatchdogInterval { get; set; } = TimeSpan.FromMinutes(1);
}
