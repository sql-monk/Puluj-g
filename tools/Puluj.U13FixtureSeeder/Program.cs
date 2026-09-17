using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Puluj.Domain.Entities;
using Puluj.Domain.Entities.Processing;
using Puluj.Domain.Enums;
using Puluj.Infrastructure;
using Puluj.Infrastructure.Persistence;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Puluj")
    ?? throw new InvalidOperationException("ConnectionStrings__Puluj must point to the disposable U13 database.");

var options = new DbContextOptionsBuilder<PulujDbContext>();
DependencyInjection.ConfigureDbContext(options, connectionString);
await using var db = new PulujDbContext(options.Options);

// The worker owns schema and reference-data migration.  This tiny, deterministic fixture only supplies evidence
// shapes which are impractical to obtain from a fresh collector: a 64-bit identifier, revisions, aggregates, and
// both located and unlocated facts.  Its unique source codes make it safe to invoke once per disposable database.
if (await db.Sources.AnyAsync(s => s.Code == "u13-e2e-a"))
{
    Console.WriteLine("U13 acceptance fixture already exists.");
    return;
}

var now = DateTimeOffset.UtcNow.AddMinutes(-5);
var category = await db.TargetCategories.OrderBy(x => x.TargetCategoryId).FirstOrDefaultAsync()
    ?? throw new InvalidOperationException("Worker migration did not seed target taxonomy.");
var targetKind = await db.EventKinds.Where(x => x.Category == EventKindCategory.Target).OrderBy(x => x.EventKindId).FirstOrDefaultAsync()
    ?? throw new InvalidOperationException("Worker migration did not seed a target event kind.");
var incidentKind = await db.EventKinds.Where(x => x.Category == EventKindCategory.Incident).OrderBy(x => x.EventKindId).FirstOrDefaultAsync()
    ?? targetKind;

var region = await db.Places.FirstOrDefaultAsync(x => x.Level == PlaceLevel.Region);
if (region is null)
{
    region = new Place
    {
        Name = "U13 тестова область",
        NameVariants = ["u13 тестова область"],
        Level = PlaceLevel.Region,
        CountryCode = "UA",
        ExternalKey = "u13:e2e-region",
        Geometry = Geo.Factory.CreatePolygon([
            new Coordinate(30.2, 50.1), new Coordinate(30.8, 50.1), new Coordinate(30.8, 50.7),
            new Coordinate(30.2, 50.7), new Coordinate(30.2, 50.1),
        ]),
        Centroid = Geo.Point(30.5, 50.4),
        RadiusKm = 50,
    };
    db.Places.Add(region);
}

var sourceA = new Source { Code = "u13-e2e-a", Name = "U13 E2E source A", Type = SourceType.Web, Url = "https://example.invalid/u13/a", TrustLevel = 1, Priority = 100 };
var sourceB = new Source { Code = "u13-e2e-b", Name = "U13 E2E source B", Type = SourceType.Web, Url = "https://example.invalid/u13/b", TrustLevel = 1, Priority = 99 };
db.Sources.AddRange(sourceA, sourceB);
await db.SaveChangesAsync();

var raws = new List<RawMessage>
{
    Raw(sourceA, "revision", "0", now.AddMinutes(-2), "U13 revision content"),
    Raw(sourceA, "revision", "e1", now.AddMinutes(-1), "U13 revision content"),
    Raw(sourceB, "same-text", "0", now, "U13 revision content"),
};
for (var i = 0; i < 120; i++)
{
    raws.Add(Raw(sourceA, $"page-{i:D3}", "0", now.AddSeconds(-i), $"U13 page fixture {i:D3}"));
}
db.RawMessages.AddRange(raws);
await db.SaveChangesAsync();

var targets = raws.Select((raw, index) => new Target
{
    TargetId = index == 0 ? 9_007_199_254_740_993L : 0,
    RawMessageId = raw.RawMessageId,
    SourceId = raw.SourceId,
    SegmentIndex = 0,
    ObservedAt = raw.PublishedAt,
    EventType = EventType.TargetObserved,
    EventKindId = targetKind.EventKindId,
    TargetCategoryId = category.TargetCategoryId,
    LocationKind = index % 7 == 0 ? LocationKind.Unknown : LocationKind.Region,
    LocationPlaceId = index % 7 == 0 ? null : region.PlaceId,
    Location = index % 7 == 0 ? null : Geo.Point(30.5, 50.4),
    LocationAccuracyKm = index % 7 == 0 ? null : 25,
    Confidence = ConfidenceLevel.High,
    ModelConfidence = ConfidenceLevel.Medium,
    ClassificationConfidence = ConfidenceLevel.Medium,
    IdentificationMethod = IdentificationMethod.Manual,
    IdentificationSource = "u13 acceptance fixture",
    ParserVersion = "u13-e2e",
    SegmentText = raw.RawText,
}).ToList();
db.Targets.AddRange(targets);
await db.SaveChangesAsync();

var bigTarget = targets[0];
var track = new TargetTrack
{
    Status = TrackStatus.Active,
    TargetCategoryId = category.TargetCategoryId,
    FirstSeenAt = bigTarget.ObservedAt,
    LastSeenAt = now,
    UpdatedAt = now,
    LastLocationKind = LocationKind.Region,
    LastLocationPlaceId = region.PlaceId,
    LastLocation = Geo.Point(30.5, 50.4),
    LastLocationAccuracyKm = 25,
    TrackGeometry = Geo.Factory.CreateLineString([new Coordinate(30.45, 50.35), new Coordinate(30.5, 50.4)]),
    DirectionKind = DirectionKind.Unknown,
    TrackConfidence = ConfidenceLevel.High,
    TargetCount = 2,
    DistinctSourceCount = 1,
    LastSourceId = sourceA.SourceId,
    LastTargetId = targets[1].TargetId,
};
db.TargetTracks.Add(track);
await db.SaveChangesAsync();
db.TrackTargets.AddRange(
    new TrackTarget { TargetTrackId = track.TargetTrackId, TargetId = bigTarget.TargetId, Sequence = 0, AssociationConfidence = 1 },
    new TrackTarget { TargetTrackId = track.TargetTrackId, TargetId = targets[1].TargetId, Sequence = 1, AssociationConfidence = 1 });
db.TargetTrackRevisions.Add(new TargetTrackRevision
{
    TargetTrackId = track.TargetTrackId,
    RevisionAt = now,
    Status = TrackStatus.Active,
    TargetCategoryId = category.TargetCategoryId,
    LastSeenAt = now,
    LastLocationKind = LocationKind.Region,
    LastLocationPlaceId = region.PlaceId,
    LastLocation = Geo.Point(30.5, 50.4),
    LastLocationAccuracyKm = 25,
    TrackGeometry = track.TrackGeometry,
    DirectionKind = DirectionKind.Unknown,
    TrackConfidence = ConfidenceLevel.High,
    TargetCount = 2,
    TargetId = targets[1].TargetId,
});

var alert = new AirAlert
{
    PlaceId = region.PlaceId,
    AlertType = AirAlertType.AirRaid,
    Level = AirAlertLevel.Red,
    StartedAt = now.AddMinutes(-1),
    SourceId = sourceA.SourceId,
    StartRawMessageId = raws[0].RawMessageId,
    SourceAlertId = "u13-active-alert",
};
db.AirAlerts.Add(alert);

var generation = new ProcessingGeneration { GenerationId = Guid.NewGuid(), IsActive = true, CreatedAt = now, PromotedAt = now, VerifiedBy = "u13-e2e" };
var incident = new Incident
{
    GenerationId = generation.GenerationId,
    EventKindId = incidentKind.EventKindId,
    State = Incident.Reported,
    FirstReportedAt = now,
    LastReportedAt = now,
    EventAt = now,
    LocationKind = LocationKind.Region,
    LocationPlaceId = region.PlaceId,
    Geometry = Geo.Point(30.5, 50.4),
    AccuracyKm = 25,
    Confidence = ConfidenceLevel.High,
    SourceCount = 1,
    Revision = 1,
    CreatedAt = now,
    UpdatedAt = now,
};
db.ProcessingGenerations.Add(generation);
db.Incidents.Add(incident);
await db.SaveChangesAsync();
db.IncidentObservations.Add(new IncidentObservation
{
    IncidentId = incident.IncidentId,
    ObservationId = Guid.NewGuid(),
    GenerationId = generation.GenerationId,
    LegacyTargetId = targets[2].TargetId,
    SourceId = targets[2].SourceId,
    Relation = IncidentObservation.Canonical,
    Score = 1,
    PolicyVersion = "u13-e2e",
    EffectiveAt = now,
    LinkedAt = now,
});
await db.SaveChangesAsync();

Console.WriteLine(JsonSerializer.Serialize(new
{
    bigTargetId = bigTarget.TargetId.ToString(), trackId = track.TargetTrackId.ToString(), incidentId = incident.IncidentId.ToString(),
    alertId = alert.AirAlertId.ToString(), rawMessageCount = raws.Count, regionId = region.PlaceId,
}));

static RawMessage Raw(Source source, string key, string revision, DateTimeOffset at, string text) => new()
{
    Source = source,
    SourceMessageId = $"{key}:{revision}",
    SourceMessageKey = key,
    SourceRevision = revision,
    PublishedAt = at,
    ReceivedAt = at,
    RawText = text,
    Url = $"https://example.invalid/u13/{source.Code}/{key}/{revision}",
    Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{source.Code}:{key}:{revision}:{text}"))),
    ProcessingStatus = ProcessingStatus.Processed,
    ProcessedAt = at,
};
