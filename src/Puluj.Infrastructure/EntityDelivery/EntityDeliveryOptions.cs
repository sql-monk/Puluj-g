namespace Puluj.Infrastructure.EntityExtraction;

public sealed class EntityDeliveryOptions
{
    public const string Section = "EntityExtractor";
    public const int MaxConcurrency = 64;

    public string Url { get; set; } = "http://entity-extractor:8080";
    public string? Token { get; set; }
    public TimeSpan DeliveryTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(2);
    // Longer than EE's 15-minute stale-run recovery window, so a reclaimed delivery can
    // recover the same delivery id instead of receiving a transient 409.
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(16);
    public int Concurrency { get; set; } = 4;
}
