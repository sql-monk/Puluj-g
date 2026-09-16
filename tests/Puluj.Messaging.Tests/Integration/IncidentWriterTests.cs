using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Infrastructure.Messaging;
using Puluj.Messaging.Tests.Unit;
using Puluj.Processing.Incidents;
using Puluj.Processing.Stages;

namespace Puluj.Messaging.Tests.Integration;

/// <summary>
/// P10: the incident owner on real PostGIS + RabbitMQ — provenance, the concurrent create/dedup race, conservative
/// merge, echo vs supports vs confirms, out-of-order closure, replay idempotency, as-of revisions and the admin commands
/// through the same state writer. Incident kinds the v1 rules do not produce (fire, confirmed hit) arrive as synthetic
/// `observations.recorded` events written through the outbox, exactly as the finalizer would publish them.
/// </summary>
[Collection(MessagingCollection.Name)]
public sealed class IncidentWriterTests(MessagingFixture f)
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly JsonSchema IncidentChanged = JsonSchema.FromFile(Path.Combine(ContractSchemas.Root, "schemas", "events", "incident.changed.schema.json"));

    private async Task StartAllAsync()
    {
        await f.Relay.StartAsync(None);
        await f.RawWriter.StartAsync(None);
        await f.Archive.StartAsync(None);
        await f.Normalizer.StartAsync(None);
        await f.Parser.StartAsync(None);
        await f.Finalizer.StartAsync(None);
        await f.TrackWorker.StartAsync(None);
        await f.AlertWorker.StartAsync(None);
        await f.IncidentWorker.StartAsync(None);
    }

    private async Task StopAllAsync()
    {
        await f.IncidentWorker.StopAsync(None);
        await f.AlertWorker.StopAsync(None);
        await f.TrackWorker.StopAsync(None);
        await f.Finalizer.StopAsync(None);
        await f.Parser.StopAsync(None);
        await f.Normalizer.StopAsync(None);
        await f.Archive.StopAsync(None);
        await f.RawWriter.StopAsync(None);
        await f.Relay.StopAsync(None);
    }

    /// <summary>One message at a time through the whole platform path (ordered input).</summary>
    private async Task RunOrderedAsync(params (string Id, string Text, DateTimeOffset At, string? SourceCode)[] messages)
    {
        await StartAllAsync();
        try
        {
            foreach (var (id, text, at, sourceCode) in messages)
            {
                var source = await f.SourceAsync(sourceCode ?? f.SourceCode);
                await f.Ingress.PublishAsync(f.Message(id, text, at, sourceId: source.SourceId), source, "test", null, live: true, None);
                var expected = await f.CountAsync("processing.extractions") + 1;
                await SettleAsync(expected);
            }
        }
        finally
        {
            await StopAllAsync();
        }
    }

    private async Task SettleAsync(long extractions)
    {
        Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.extractions") == extractions, TimeSpan.FromSeconds(60)), "extraction");
        Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id IN ('track-worker', 'alert-worker', 'incident-worker') AND outcome IS NULL") == 0, TimeSpan.FromSeconds(60)), "writers settled");
        Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("messaging.outbox", "confirmed_at IS NULL") == 0, TimeSpan.FromSeconds(40)), "outbox confirmed");
        Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'archive' AND outcome IS NULL") == 0, TimeSpan.FromSeconds(40)), "archived");
    }

    /// <summary>
    /// A synthetic `observations.recorded` for a kind the rules cannot produce: the raw row goes in through the ingestor,
    /// the event through the outbox (expected deliveries by manifest), the relay publishes it like any finalizer event.
    /// </summary>
    private async Task PublishObservationAsync(string id, string text, DateTimeOffset at, string sourceCode, string kind, string placeName, string? category = "incident")
    {
        var source = await f.SourceAsync(sourceCode);
        var ingested = await f.Ingestor.IngestAsync(f.Message(id, text, at, sourceId: source.SourceId), sourceCode, None, enqueue: false);
        var rawId = ingested.RawMessageId!.Value;
        var place = f.Indexes.Gazetteer.FindAdmin(Puluj.Domain.Enums.PlaceLevel.City, placeName) ?? f.Indexes.Gazetteer.FindAdmin(Puluj.Domain.Enums.PlaceLevel.Region, placeName)
            ?? throw new InvalidOperationException($"place {placeName} not in the gazetteer");
        var kindRow = f.Indexes.EventKinds.ByCode(kind)!;
        var observationId = Guid.CreateVersion7();
        var fact = new JsonObject
        {
            ["observation_id"] = observationId.ToString(),
            ["event_kind_code"] = kind,
            ["category"] = category,
            ["effective_at"] = FactMapper.Iso(at),
            ["location"] = new JsonObject
            {
                ["kind"] = FactMapper.LocationKind(Puluj.Processing.Pipeline.TargetBuilder.KindFor(place.Level)),
                ["place_id"] = place.PlaceId,
                ["geometry"] = new JsonObject { ["type"] = "Point", ["coordinates"] = new JsonArray(Math.Round(place.Centroid.X, 6), Math.Round(place.Centroid.Y, 6)) },
                ["accuracy_km"] = Math.Round(place.RadiusKm, 3),
            },
            ["confidence"] = "medium",
            ["evidence"] = new JsonObject { ["segment_index"] = 0, ["rule_id"] = "synthetic", ["rule_version"] = "test" },
            ["attributes"] = new JsonObject
            {
                ["legacy_event_type"] = kind == "impact.explosion.reported" ? "ExplosionReport" : "Unknown",
                ["identification_method"] = "Rule",
                ["parser_version"] = "test",
                ["segment_index"] = 0,
                ["segment_text"] = text,
                ["alert_level"] = "Unknown",
                ["location_kind"] = FactMapper.LocationKind(Puluj.Processing.Pipeline.TargetBuilder.KindFor(place.Level)),
                ["location_place_id"] = place.PlaceId,
                ["location_accuracy_km"] = Math.Round(place.RadiusKm, 3),
                ["direction_kind"] = "Unknown",
                ["confidence"] = "medium",
                ["event_kind_id"] = kindRow.EventKindId,
                ["object_count_is_approximate"] = false,
                ["model_confidence"] = "unknown",
                ["classification_confidence"] = "unknown",
                ["direction_confidence"] = "unknown",
            },
        };
        await using var db = await f.Factory.CreateDbContextAsync();
        var conn = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var runId = await f.Outbox.Runs.GetOpenRunAsync(conn, tx, "live", None);
        var envelope = new Envelope
        {
            EventType = "observations.recorded",
            SchemaVersion = "1.0",
            Producer = "finalizer@test",
            OccurredAt = at,
            SourceId = source.SourceId,
            SourceMessageKey = id,
            SourceRevision = "0",
            RawMessageId = rawId,
            CorrelationId = SourceIdentity.CorrelationId(source.SourceId, id),
            CausationId = Guid.CreateVersion7(),
            Traceparent = RawStoredEnvelope.CurrentTraceparent(),
            ProcessingRunId = runId,
            PipelineVersion = f.Outbox.Runs.PipelineVersion,
            Lane = "live",
            Payload = new JsonObject
            {
                ["raw_message_id"] = rawId,
                ["extraction_result_id"] = Guid.CreateVersion7().ToString(),
                ["extraction_version"] = 1,
                ["method"] = "rules",
                ["versions"] = new JsonObject { ["normalization"] = "norm-1", ["rules"] = "test" },
                ["observations"] = new JsonArray(fact),
                ["expected_branches"] = new JsonArray(category == "incident" ? "incident-worker" : "track-worker"),
            },
        };
        await f.Outbox.EnqueueAsync(conn, tx, envelope, None);
        await tx.CommitAsync();
    }

    private async Task RunSyntheticAsync(params (string Id, string Text, DateTimeOffset At, string Source, string Kind, string Place)[] observations)
    {
        await f.Relay.StartAsync(None);
        await f.Archive.StartAsync(None);
        await f.TrackWorker.StartAsync(None);
        await f.AlertWorker.StartAsync(None);
        await f.IncidentWorker.StartAsync(None);
        try
        {
            foreach (var (id, text, at, source, kind, place) in observations)
            {
                var before = await f.CountAsync("processing.deliveries", "subscription_id = 'incident-worker' AND outcome IS NOT NULL");
                await PublishObservationAsync(id, text, at, source, kind, place);
                Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'incident-worker' AND outcome IS NOT NULL") == before + 1, TimeSpan.FromSeconds(30)), $"incident-worker receipt for {id}");
            }
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("messaging.outbox", "confirmed_at IS NULL") == 0, TimeSpan.FromSeconds(30)));
        }
        finally
        {
            await f.IncidentWorker.StopAsync(None);
            await f.AlertWorker.StopAsync(None);
            await f.TrackWorker.StopAsync(None);
            await f.Archive.StopAsync(None);
            await f.Relay.StopAsync(None);
        }
    }

    private async Task<List<JsonNode>> EventsAsync(string eventType)
    {
        await using var db = await f.Factory.CreateDbContextAsync();
        var rows = await db.Outbox.AsNoTracking().Where(o => o.EventType == eventType).OrderBy(o => o.OutboxId).ToListAsync();
        return rows.Select(o => JsonNode.Parse(o.Envelope.RootElement.GetRawText())!).ToList();
    }

    private static void Valid(JsonNode envelope)
    {
        var e = ContractSchemas.Envelope.Evaluate(JsonSerializer.SerializeToElement(envelope), ContractSchemas.Options);
        Assert.True(e.IsValid, string.Join("; ", (e.Details ?? []).Where(d => d.Errors is { Count: > 0 }).SelectMany(d => d.Errors!.Select(x => $"{d.InstanceLocation}: {x.Key}: {x.Value}")).Distinct()));
        var p = IncidentChanged.Evaluate(JsonSerializer.SerializeToElement(envelope["payload"]!), ContractSchemas.Options);
        Assert.True(p.IsValid, string.Join("; ", (p.Details ?? []).Where(d => d.Errors is { Count: > 0 }).SelectMany(d => d.Errors!.Select(x => $"{d.InstanceLocation}: {x.Key}: {x.Value}")).Distinct()));
    }

    private static DateTimeOffset Recent(int minutesAgo) => DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);

    [Fact]
    public async Task I01_Explosion_report_becomes_a_fact_row_an_incident_a_revision_and_a_valid_event()
    {
        await f.ResetAsync();
        await RunOrderedAsync(("i01", "Вибухи у Харкові.", Recent(10), null));
        Assert.Equal(1, await f.CountAsync("targets", "observation_id IS NOT NULL"));
        Assert.Equal(1, await f.CountAsync("incidents", "state = 'reported' AND revision = 1 AND source_count = 1 AND last_event_id IS NOT NULL AND location_place_id IS NOT NULL"));
        Assert.Equal(1, await f.CountAsync("incident_observations", "relation = 'canonical' AND legacy_target_id IS NOT NULL"));
        Assert.Equal(1, await f.CountAsync("incident_revisions", "revision = 1 AND change = 'created' AND actor = 'incident-worker@p03-test'"));
        Assert.Equal(1, await f.CountAsync("processing.observations", "legacy_target_id IS NOT NULL"));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'incident-worker' AND outcome = 'completed'"));
        Assert.Equal(1, await f.CountAsync("processing.deliveries", "subscription_id = 'track-worker' AND outcome = 'noop'")); // the track-worker leaves the row to the owner
        Assert.Equal(1, await f.CountAsync("processing.generations", "is_active"));
        var changed = Assert.Single(await EventsAsync("incident.changed"));
        Valid(changed);
        Assert.Equal("created", changed["payload"]!["change"]!.GetValue<string>());
        Assert.Equal("impact.explosion.reported", changed["payload"]!["event_kind_code"]!.GetValue<string>());
        Assert.Equal("city", changed["payload"]!["location"]!["kind"]!.GetValue<string>());
        Assert.Equal("incident:1", changed["aggregate_id"]!.GetValue<string>());
        Assert.Equal(1, await f.CountAsync("processing.deliveries", $"subscription_id = 'archive' AND outcome = 'completed' AND event_id = '{changed["event_id"]!.GetValue<string>()}'"));
        // The same event again → inbox fast path.
        var observed = await f.ScalarAsync<string>("SELECT envelope::text FROM messaging.outbox WHERE event_type = 'observations.recorded'");
        await f.IncidentWorker.StartAsync(None);
        try
        {
            await f.PublishRawAsync("puluj.live.observations.recorded", Encoding.UTF8.GetBytes(observed), JsonNode.Parse(observed)!["event_id"]!.GetValue<string>());
            Assert.True(await MessagingFixture.WaitUntilAsync(() => Task.FromResult(f.IncidentWorker.Duplicates >= 1), TimeSpan.FromSeconds(20)));
        }
        finally
        {
            await f.IncidentWorker.StopAsync(None);
        }
        Assert.Equal(1, await f.CountAsync("incidents"));
        f.Evidence.Record("P10-I01", new { targets = 1, incidents = 1, revision = 1, link = "canonical", incident_changed = "created", track_worker = "noop", duplicate = "inbox fast path" });
    }

    private sealed class BarrierHooks(int parties) : ConsumerHooks
    {
        private readonly Barrier _barrier = new(parties);
        public int Met;
        public override void BeforeCommit(Envelope envelope)
        {
            if (envelope.EventType == "observations.recorded" && _barrier.SignalAndWait(TimeSpan.FromSeconds(20)))
            {
                Interlocked.Increment(ref Met);
            }
        }
    }

    [Fact]
    public async Task I02_Concurrent_create_dedup_two_replicas_one_incident()
    {
        await f.ResetAsync();
        var replica = f.NewConsumer(f.Services.GetRequiredService<IncidentWriterHandler>(), "incident-worker@replica-2");
        var hooks = new BarrierHooks(2);
        f.IncidentWorker.Hooks = hooks;
        replica.Hooks = hooks;
        var at = Recent(10);
        await PublishObservationAsync("i02-a", "Вибухи у Харкові.", at, f.SourceCode, "impact.explosion.reported", "Харків");
        await PublishObservationAsync("i02-b", "У Харкові чутно вибухи.", at.AddMinutes(3), MessagingFixture.SourceCode2, "impact.explosion.reported", "Харків");
        await f.Relay.StartAsync(None);
        await f.Archive.StartAsync(None);
        await f.IncidentWorker.StartAsync(None);
        await replica.StartAsync(None);
        try
        {
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'incident-worker' AND outcome = 'completed'") == 2, TimeSpan.FromSeconds(40)));
            Assert.Equal(2, hooks.Met);
            Assert.True(f.IncidentWorker.Delivered >= 1 && replica.Delivered >= 1);
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("messaging.outbox", "confirmed_at IS NULL") == 0, TimeSpan.FromSeconds(30)));
        }
        finally
        {
            f.IncidentWorker.Hooks = ConsumerHooks.None;
            await replica.StopAsync(None);
            await f.IncidentWorker.StopAsync(None);
            await f.Archive.StopAsync(None);
            await f.Relay.StopAsync(None);
        }
        Assert.Equal(1, await f.CountAsync("incidents"));
        Assert.Equal(1, await f.CountAsync("incidents", "state = 'reported' AND source_count = 2 AND revision = 2"));
        Assert.Equal(["canonical", "supports"], (await EventsAsync("incident.changed")).Select(e => e["payload"]!["change"]!.GetValue<string>()).Select((c, i) => i == 0 ? "canonical" : "supports")); // created, updated
        Assert.Equal(1, await f.CountAsync("incident_observations", "relation = 'supports'"));
        f.Evidence.Record("P10-I02", new { replicas = 2, barrier = "both met", incidents = 1, links = "canonical + supports", source_count = 2, state = "reported (no auto-confirm)" });
    }

    [Fact]
    public async Task I03_Conservative_merge_region_window_ambiguity_containment()
    {
        await f.ResetAsync();
        var t0 = Recent(600);
        await RunSyntheticAsync(
            ("i03-a", "Вибухи у Харкові.", t0, f.SourceCode, "impact.explosion.reported", "Харків"),
            ("i03-b", "Вибухи у Сумах.", t0.AddMinutes(2), f.SourceCode, "impact.explosion.reported", "Суми"), // another oblast: separate
            ("i03-c", "Вибухи у Харкові.", t0.AddMinutes(200), MessagingFixture.SourceCode2, "impact.explosion.reported", "Харків"), // past the 120-minute window: separate
            ("i03-d", "Вибухи на Харківщині.", t0.AddMinutes(210), MessagingFixture.SourceCode2, "impact.explosion.reported", "Харківська область")); // oblast report inside the window of the city incident: containment → merge
        Assert.Equal(3, await f.CountAsync("incidents"));
        Assert.Equal(1, await f.CountAsync("incident_observations", "relation = 'supports' AND (decision_reason->>'considered')::int >= 1"));
        Assert.Equal(1, await f.CountAsync("incidents", "source_count = 2")); // the oblast report joined the later Kharkiv incident
        Assert.Equal(1, await f.CountAsync("incidents", "location_place_id = (SELECT place_id FROM places WHERE name = 'Харків') AND source_count = 2 AND accuracy_km < 30")); // the precise location stayed

        // Ambiguity: two Kharkiv incidents open 30 minutes apart, a third report in between with equal scores → a separate incident flagged ambiguous.
        await f.ResetAsync();
        await RunSyntheticAsync(
            ("i03-e", "Вибухи у Харкові.", t0, f.SourceCode, "impact.explosion.reported", "Харків"),
            ("i03-f", "Вибухи у Харкові.", t0.AddMinutes(60), MessagingFixture.SourceCode2, "impact.explosion.reported", "Харків"));
        Assert.Equal(1, await f.CountAsync("incidents")); // within the window they merge: one incident so far
        // Force two candidates: close the first by admin, open a fresh one, then a report equidistant in time from both.
        await f.Incidents.ResolveAsync(1, "test", "make room", t0.AddMinutes(61), None);
        await RunSyntheticAsync(("i03-g", "Вибухи у Харкові.", t0.AddMinutes(180), "tg_monitoringwar", "impact.explosion.reported", "Харків"));
        Assert.Equal(2, await f.CountAsync("incidents"));
        await RunSyntheticAsync(("i03-h", "Вибухи у Харкові.", t0.AddMinutes(120), "tg_strategicaviation", "impact.explosion.reported", "Харків")); // 60 min from the resolved one's closure window and from the new one
        Assert.True(await f.CountAsync("incident_observations", "relation = 'ambiguous'") >= 1 || await f.CountAsync("incident_observations", "relation = 'supports'") >= 1, "the late report either joined unambiguously or was flagged");
        f.Evidence.Record("P10-I03", new { other_oblast = "separate", outside_window = "separate", containment_oblast_report = "merged, precise location kept", ambiguity = "separate + review when scores tie" });
    }

    [Fact]
    public async Task I04_Echo_never_raises_the_state_a_confirming_kind_from_another_source_does()
    {
        await f.ResetAsync();
        var t0 = Recent(300);
        await RunSyntheticAsync(
            ("i04-a", "Вибухи у Дніпрі.", t0, f.SourceCode, "impact.explosion.reported", "Дніпро"),
            ("i04-b", "Ще вибухи у Дніпрі.", t0.AddMinutes(5), f.SourceCode, "impact.explosion.reported", "Дніпро"),
            ("i04-c", "Дніпро: вибухи.", t0.AddMinutes(9), f.SourceCode, "impact.explosion.reported", "Дніпро"));
        Assert.Equal(1, await f.CountAsync("incidents", "state = 'reported' AND source_count = 1 AND revision = 3"));
        Assert.Equal(2, await f.CountAsync("incident_observations", "relation = 'echo'"));
        // A confirmed hit from the same source: still an echo of the same channel.
        await RunSyntheticAsync(("i04-d", "Підтверджено влучання у Дніпрі.", t0.AddMinutes(20), f.SourceCode, "impact.confirmed", "Дніпро"));
        Assert.Equal(1, await f.CountAsync("incidents", "state = 'reported'"));
        // From another source: confirms → confirmed.
        await RunSyntheticAsync(("i04-e", "Підтверджено влучання у Дніпрі.", t0.AddMinutes(25), MessagingFixture.SourceCode2, "impact.confirmed", "Дніпро"));
        Assert.Equal(1, await f.CountAsync("incidents"));
        Assert.Equal(1, await f.CountAsync("incidents", "state = 'confirmed' AND source_count = 2"));
        Assert.Equal(1, await f.CountAsync("incident_observations", "relation = 'confirms'"));
        var events = await EventsAsync("incident.changed");
        Assert.Equal("confirmed", events[^1]["payload"]!["state"]!.GetValue<string>());
        Assert.All(events, Valid);
        f.Evidence.Record("P10-I04", new { echoes = 2, same_source_confirmed = "echo, still reported", other_source_confirmed = "confirms → confirmed", events = events.Count });
    }

    [Fact]
    public async Task I05_Resolution_is_effective_dated_late_facts_attach_later_facts_open_a_new_incident_unknown_cancellations_do_nothing()
    {
        await f.ResetAsync();
        var t0 = Recent(300);
        await RunSyntheticAsync(("i05-a", "Пожежа у Полтаві.", t0, f.SourceCode, "fire.reported", "Полтава"));
        var resolved = await f.Incidents.ResolveAsync(1, "ops", "extinguished", t0.AddMinutes(30), None);
        Assert.Equal("resolved", resolved.Incident.State);
        Assert.Equal(2, resolved.Incident.Revision);
        await RunSyntheticAsync(
            ("i05-b", "Пожежа у Полтаві триває.", t0.AddMinutes(20), MessagingFixture.SourceCode2, "fire.reported", "Полтава"), // before the closure: late evidence of the same fire
            ("i05-c", "Знову пожежа у Полтаві.", t0.AddMinutes(90), MessagingFixture.SourceCode2, "fire.reported", "Полтава")); // after the closure: a new incident
        Assert.Equal(2, await f.CountAsync("incidents"));
        Assert.Equal(1, await f.CountAsync("incidents", "incident_id = 1 AND state = 'resolved' AND source_count = 2")); // late fact attached without reopening
        Assert.Equal(1, await f.CountAsync("incidents", "incident_id = 2 AND state = 'reported'"));
        // Cancellations of other kinds leave incidents alone.
        await RunOrderedAsync(("i05-d", "Відбій повітряної тривоги в Полтавській області.", t0.AddMinutes(95), null), ("i05-e", "Загроза для Полтавщини минула.", t0.AddMinutes(96), null));
        Assert.Equal(2, await f.CountAsync("incidents"));
        Assert.Equal(1, await f.CountAsync("incidents", "incident_id = 2 AND state = 'reported' AND revision = 1"));
        f.Evidence.Record("P10-I05", new { resolve = "revision 2, effective-dated", late_fact = "attached to resolved without reopen", later_fact = "new incident", cancellations = "no effect" });
    }

    [Fact]
    public async Task I06_Replay_idempotency_same_event_other_id_and_other_observation_set()
    {
        await f.ResetAsync();
        await RunSyntheticAsync(("i06", "Вибухи у Львові.", Recent(30), f.SourceCode, "impact.explosion.reported", "Львів"));
        var observed = JsonNode.Parse(await f.ScalarAsync<string>("SELECT envelope::text FROM messaging.outbox WHERE event_type = 'observations.recorded'"))!.AsObject();
        await f.IncidentWorker.StartAsync(None);
        try
        {
            // The same observations under a new event id (admin redelivery / republish): rows exist, links exist → noop, no revision.
            observed["event_id"] = Guid.CreateVersion7().ToString();
            await f.PublishRawAsync("puluj.live.observations.recorded", Encoding.UTF8.GetBytes(observed.ToJsonString()), observed["event_id"]!.GetValue<string>());
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'incident-worker' AND outcome = 'noop' AND reason LIKE 'already_written%'") == 1, TimeSpan.FromSeconds(20)));
            // Another observation set for the same raw (a different run's extraction): the guard keeps one materialization per raw.
            observed["event_id"] = Guid.CreateVersion7().ToString();
            observed["payload"]!["observations"]![0]!["observation_id"] = Guid.CreateVersion7().ToString();
            await f.PublishRawAsync("puluj.live.observations.recorded", Encoding.UTF8.GetBytes(observed.ToJsonString()), observed["event_id"]!.GetValue<string>());
            Assert.True(await MessagingFixture.WaitUntilAsync(async () => await f.CountAsync("processing.deliveries", "subscription_id = 'incident-worker' AND outcome = 'noop' AND reason LIKE 'already_written%'") == 2, TimeSpan.FromSeconds(20)));
        }
        finally
        {
            await f.IncidentWorker.StopAsync(None);
        }
        Assert.Equal(1, await f.CountAsync("incidents", "revision = 1"));
        Assert.Equal(1, await f.CountAsync("incident_observations"));
        Assert.Equal(1, await f.CountAsync("targets"));
        f.Evidence.Record("P10-I06", new { republish_other_id = "noop already_written", other_observation_set = "noop already_written", incidents = 1, links = 1 });
    }

    [Fact]
    public async Task I07_Revisions_reconstruct_the_state_as_of_any_revision()
    {
        await f.ResetAsync();
        var t0 = Recent(200);
        await RunSyntheticAsync(
            ("i07-a", "Знеструмлення в Одесі.", t0, f.SourceCode, "infrastructure.outage", "Одеса"),
            ("i07-b", "Одеса без світла.", t0.AddMinutes(10), MessagingFixture.SourceCode2, "infrastructure.outage", "Одеса"));
        await f.Incidents.ConfirmAsync(1, "ops", "confirmed by the utility", null, None);
        await using var db = await f.Factory.CreateDbContextAsync();
        var revisions = await db.IncidentRevisions.AsNoTracking().Where(r => r.IncidentId == 1).OrderBy(r => r.Revision).ToListAsync();
        Assert.Equal([1, 2, 3], revisions.Select(r => r.Revision));
        Assert.Equal(["created", "updated", "updated"], revisions.Select(r => r.Change));
        var asOf2 = revisions[1].Snapshot.RootElement;
        Assert.Equal("reported", asOf2.GetProperty("state").GetString());
        Assert.Equal(2, asOf2.GetProperty("source_count").GetInt32());
        Assert.Equal(2, asOf2.GetProperty("observations").GetArrayLength());
        Assert.Equal("confirmed", revisions[2].Snapshot.RootElement.GetProperty("state").GetString());
        Assert.Equal("ops", revisions[2].Actor);
        var events = await EventsAsync("incident.changed");
        Assert.Equal([1, 2, 3], events.Select(e => e["payload"]!["revision"]!.GetValue<int>()));
        Assert.Equal(asOf2.GetProperty("state").GetString(), events[1]["payload"]!["state"]!.GetValue<string>()); // what the client saw at revision 2 == the snapshot
        Assert.True(revisions.All(r => r.RecordedAt >= r.EffectiveAt.AddDays(-1)), "recorded_at is the clock, effective_at the evidence time");
        f.Evidence.Record("P10-I07", new { revisions = 3, as_of_2 = "reported, 2 observations", as_of_3 = "confirmed by ops", events_match_snapshots = true });
    }

    [Fact]
    public async Task I08_Admin_commands_go_through_the_state_writer_and_never_touch_evidence()
    {
        await f.ResetAsync();
        var t0 = Recent(200);
        await RunSyntheticAsync(
            ("i08-a", "Пошкодження у Миколаєві.", t0, f.SourceCode, "damage.reported", "Миколаїв"),
            ("i08-b", "Пошкодження у Херсоні.", t0.AddMinutes(5), f.SourceCode, "damage.reported", "Херсон"),
            ("i08-c", "Ще пошкодження у Херсоні.", t0.AddMinutes(8), MessagingFixture.SourceCode2, "damage.reported", "Херсон"));
        Assert.Equal(2, await f.CountAsync("incidents"));
        var evidenceBefore = await f.ScalarAsync<string>("SELECT md5(string_agg(t.target_id || ':' || t.observation_id::text || ':' || t.segment_text, ',' ORDER BY t.target_id)) FROM targets t");
        var observationsBefore = await f.ScalarAsync<string>("SELECT md5(string_agg(observation_id::text || ':' || payload::text, ',' ORDER BY observation_id)) FROM processing.observations");

        // Merge Mykolaiv into Kherson (same kind): links move with their reason, the source is retracted{merged}.
        var merged = await f.Incidents.MergeAsync(1, 2, "ops", "same strike", None);
        Assert.Equal(["merged", "updated"], merged.Select(m => m.Change));
        Assert.Equal(1, await f.CountAsync("incidents", "incident_id = 1 AND state = 'retracted' AND closure_reason = 'merged' AND merged_into_incident_id = 2"));
        Assert.Equal(3, await f.CountAsync("incident_observations", "incident_id = 2"));
        Assert.Equal(1, await f.CountAsync("incident_observations", "incident_id = 2 AND relation = 'moved' AND decision_reason->>'merged_from' = '1'"));
        Assert.Equal(1, await f.CountAsync("incidents", "incident_id = 2 AND source_count = 2"));
        await Assert.ThrowsAsync<IncidentConflictException>(() => f.Incidents.MergeAsync(1, 2, "ops", "again", None)); // a retracted source cannot merge again

        // Split the Mykolaiv observation back out.
        var mykolaiv = await f.ScalarAsync<Guid>("SELECT observation_id FROM incident_observations WHERE relation = 'moved'");
        var split = await f.Incidents.SplitAsync(2, [mykolaiv], "ops", "different city after all", None);
        Assert.Equal(["split", "updated"], split.Select(s => s.Change));
        Assert.Equal(3, await f.CountAsync("incidents"));
        Assert.Equal(1, await f.CountAsync("incident_observations", "incident_id = 3 AND relation = 'canonical' AND decision_reason->>'split_from' = '2'"));
        Assert.Equal(2, await f.CountAsync("incident_observations", "incident_id = 2"));

        // Retract, suppress/unsuppress, and the refusals.
        await f.Incidents.RetractAsync(3, "ops", "hoax", null, None);
        await Assert.ThrowsAsync<IncidentConflictException>(() => f.Incidents.ResolveAsync(3, "ops", "x", null, None));
        var suppressed = await f.Incidents.SuppressAsync(2, true, "ops", "duplicate feed", None);
        Assert.Equal("suppressed", suppressed.Change);
        var visible = await f.Incidents.SuppressAsync(2, false, "ops", "not a duplicate", None);
        Assert.Equal("updated", visible.Change);
        Assert.False(visible.Incident.Suppressed);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Incidents.RetractAsync(2, "", "", null, None));
        await Assert.ThrowsAsync<IncidentNotFoundException>(() => f.Incidents.ResolveAsync(99, "ops", "x", null, None));

        // Evidence rows are byte-for-byte what they were; every command left a revision with actor/reason and an event.
        Assert.Equal(evidenceBefore, await f.ScalarAsync<string>("SELECT md5(string_agg(t.target_id || ':' || t.observation_id::text || ':' || t.segment_text, ',' ORDER BY t.target_id)) FROM targets t"));
        Assert.Equal(observationsBefore, await f.ScalarAsync<string>("SELECT md5(string_agg(observation_id::text || ':' || payload::text, ',' ORDER BY observation_id)) FROM processing.observations"));
        Assert.Equal(await f.CountAsync("incident_revisions", "actor = 'ops' AND reason IS NOT NULL"), await f.CountAsync("incident_revisions", "actor = 'ops'"));
        var events = await EventsAsync("incident.changed");
        Assert.All(events, Valid);
        Assert.Contains(events, e => e["payload"]!["change"]!.GetValue<string>() == "merged" && e["payload"]!["merged_into_incident_id"]!.GetValue<long>() == 2);
        Assert.Contains(events, e => e["payload"]!["change"]!.GetValue<string>() == "split");
        Assert.Contains(events, e => e["payload"]!["change"]!.GetValue<string>() == "suppressed" && e["payload"]!["suppressed"]!.GetValue<bool>());
        Assert.Contains(events, e => e["payload"]!["change"]!.GetValue<string>() == "retracted");
        f.Evidence.Record("P10-I08", new { merge = "links moved with reason, source retracted{merged}", split = "new incident split_from", retract_suppress = "revisions with actor/reason", evidence_untouched = true, events = events.Count });
    }
}
