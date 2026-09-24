using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.EntityExtraction;
using Puluj.Infrastructure.Persistence;

namespace Puluj.Integration.Tests;

[Collection(PipelineCollection.Name)]
public sealed class EntityDeliveryTests(PipelineFixture fixture) : IAsyncLifetime
{
    private IServiceProvider Services => fixture.Services!;
    private IDbContextFactory<PulujDbContext> Factory => Services.GetRequiredService<IDbContextFactory<PulujDbContext>>();

    public Task InitializeAsync() => fixture.ResetDataAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Insert_enqueues_on_commit_and_rollback_enqueues_nothing_without_touching_legacy_state()
    {
        long committedId;
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var sourceId = await db.Sources.Select(x => x.SourceId).FirstAsync();
            var raw = Raw(sourceId, "ee-committed");
            db.RawMessages.Add(raw);
            await db.SaveChangesAsync();
            committedId = raw.RawMessageId;
        }

        await using (var db = await Factory.CreateDbContextAsync())
        {
            var delivery = await db.EntityDeliveries.SingleAsync(x => x.RawMessageId == committedId);
            Assert.Equal("live", delivery.Origin);
            Assert.Equal("pending", delivery.Status);
            var raw = await db.RawMessages.SingleAsync(x => x.RawMessageId == committedId);
            Assert.Equal(ProcessingStatus.Pending, raw.ProcessingStatus);
            Assert.Equal(0, raw.Attempts);
            Assert.Null(raw.ClaimedAt);
            Assert.Null(raw.ClaimedBy);
        }

        long rolledBackId;
        await using (var db = await Factory.CreateDbContextAsync())
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var sourceId = await db.Sources.Select(x => x.SourceId).FirstAsync();
            var raw = Raw(sourceId, "ee-rolled-back");
            db.RawMessages.Add(raw);
            await db.SaveChangesAsync();
            rolledBackId = raw.RawMessageId;
            Assert.True(await db.EntityDeliveries.AnyAsync(x => x.RawMessageId == rolledBackId));
            await tx.RollbackAsync();
        }

        await using (var db = await Factory.CreateDbContextAsync())
        {
            Assert.False(await db.RawMessages.AnyAsync(x => x.RawMessageId == rolledBackId));
            Assert.False(await db.EntityDeliveries.AnyAsync(x => x.RawMessageId == rolledBackId));
        }
    }

    [Fact]
    public async Task Manual_enqueue_overloads_create_fresh_delivery_ids_and_bound_selection()
    {
        long firstId;
        long secondId;
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var sourceId = await db.Sources.Select(x => x.SourceId).FirstAsync();
            var first = Raw(sourceId, "ee-manual-1");
            var second = Raw(sourceId, "ee-manual-2");
            db.AddRange(first, second);
            await db.SaveChangesAsync();
            firstId = first.RawMessageId;
            secondId = second.RawMessageId;

            Assert.Equal(1, await ScalarAsync(db, "SELECT ee_enqueue_raw_message({0}) AS \"Value\"", firstId));
            Assert.Equal(2, await ScalarAsync(db, "SELECT ee_enqueue_raw_messages(ARRAY[{0},{1}]::bigint[]) AS \"Value\"", firstId, secondId));
            Assert.Equal(1, await ScalarAsync(db,
                "SELECT ee_enqueue_raw_messages({0}, {1}, NULL, NULL, NULL, 1) AS \"Value\"", firstId, secondId));
        }

        await using (var db = await Factory.CreateDbContextAsync())
        {
            var manual = await db.EntityDeliveries.Where(x => x.Origin == "manual").ToListAsync();
            Assert.Equal(4, manual.Count);
            Assert.Equal(4, manual.Select(x => x.DeliveryId).Distinct().Count());
            Assert.Equal(2, await db.EntityDeliveries.CountAsync(x => x.Origin == "live"));
        }
    }

    [Fact]
    public async Task Migration_seeds_concrete_definitions_and_distinct_physical_tables()
    {
        await using var db = await Factory.CreateDbContextAsync();
        var definitions = await db.EntityDefinitions.OrderBy(x => x.EntityDefinitionId).ToListAsync();
        Assert.Equal(
            ["ee_targets", "ee_tracks", "ee_alerts", "ee_impacts", "ee_explosions", "ee_air_defense_actions", "ee_launches", "ee_takeoffs"],
            definitions.Select(x => x.TableName).ToArray());
        foreach (var definition in definitions)
        {
            Assert.Equal(definition.TableName, await db.Database.SqlQueryRaw<string>(
                "SELECT to_regclass({0})::text AS \"Value\"", definition.TableName).SingleAsync());
            Assert.True(await db.Database.SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name={0} AND column_name={1}) AS \"Value\"",
                definition.TableName, definition.EntityName + "Id").SingleAsync());
        }
        Assert.DoesNotContain(definitions, x => x.EntityName == "event");
    }

    [Fact]
    public async Task Reader_can_read_only_entity_registry_and_entity_tables()
    {
        await using var db = await Factory.CreateDbContextAsync();
        Assert.True(await TableSelectPrivilegeAsync(db, "public.ee_entity_definitions"));
        Assert.True(await TableSelectPrivilegeAsync(db, "public.ee_targets"));
        Assert.False(await TablePrivilegeAsync(db, "public.app_settings", "SELECT", "puluj_ee"));
        Assert.False(await TablePrivilegeAsync(db, "public.raw_messages", "SELECT", "puluj_ee"));
        Assert.False(await TablePrivilegeAsync(db, "public.sources", "SELECT", "puluj_ee"));
        Assert.False(await TablePrivilegeAsync(db, "public.llm_requests", "UPDATE", "puluj_ee"));
        Assert.True(await db.Database.SqlQueryRaw<bool>(
            "SELECT has_function_privilege('puluj_ee', 'public.ee_get_llm_settings()', 'EXECUTE') AS \"Value\"").SingleAsync());
        Assert.True(await db.Database.SqlQueryRaw<bool>(
            "SELECT has_function_privilege('puluj_ee', 'public.ee_finalize_llm_request(bigint, text, integer, integer, integer, integer, integer, integer, numeric, integer, text, jsonb, text)', 'EXECUTE') AS \"Value\"").SingleAsync());
        foreach (var controlTable in new[]
                 {
                     "ee_delivery_queue", "ee_delivery_attempts", "ee_extractors",
                     "ee_processing_runs", "ee_extractor_runs", "ee_entity_writes",
                 })
        {
            Assert.False(await TableSelectPrivilegeAsync(db, $"public.{controlTable}"));
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(
            """
            SELECT ee_create_entity_definition(
                'privilegeProbe',
                '[{{"name":"label","type":"text","required":false}},{{"name":"targetURL","type":"text","required":false}}]'::jsonb,
                '{{}}'::jsonb,
                true)
            """);
        Assert.True(await TableSelectPrivilegeAsync(db, "public.ee_privilege_probes"));
        Assert.True(await db.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='ee_privilege_probes' AND column_name='privilegeProbeId') AS \"Value\"").SingleAsync());
        Assert.True(await db.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='ee_privilege_probes' AND column_name='target_u_r_l') AS \"Value\"").SingleAsync());
        await db.Database.ExecuteSqlRawAsync(
            """
            SELECT ee_create_entity_definition(repeat('a', 57) || 'x', '[{{"name":"label","type":"text"}}]'::jsonb, '{{}}'::jsonb, true);
            SELECT ee_create_entity_definition(repeat('a', 57) || 'y', '[{{"name":"label","type":"text"}}]'::jsonb, '{{}}'::jsonb, true)
            """);
        Assert.True(await db.Database.SqlQueryRaw<bool>(
            """
            SELECT count(DISTINCT indexname) = 2 AND max(length(indexname)) <= 63 AS "Value"
            FROM pg_indexes
            WHERE tablename IN ('ee_' || repeat('a', 57) || 'xs', 'ee_' || repeat('a', 57) || 'ys')
            """).SingleAsync());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Entity_extractor_role_can_write_runtime_audit_and_entities_but_not_legacy_outputs()
    {
        long rawMessageId;
        int sourceId;
        Guid deliveryId;
        string ownerConnectionString;
        await using (var db = await Factory.CreateDbContextAsync())
        {
            sourceId = await db.Sources.Select(x => x.SourceId).FirstAsync();
            var raw = Raw(sourceId, "ee-role-runtime");
            db.RawMessages.Add(raw);
            await db.SaveChangesAsync();
            rawMessageId = raw.RawMessageId;
            deliveryId = await db.EntityDeliveries.Where(x => x.RawMessageId == rawMessageId).Select(x => x.DeliveryId).SingleAsync();
            ownerConnectionString = db.Database.GetConnectionString()!;
            Assert.False(await TablePrivilegeAsync(db, "public.targets", "INSERT", "puluj_ee"));
            Assert.False(await TablePrivilegeAsync(db, "public.raw_messages", "UPDATE", "puluj_ee"));
        }

        var eeConnectionString = new NpgsqlConnectionStringBuilder(ownerConnectionString)
        {
            Username = "puluj_ee",
            Password = "puluj_ee",
        }.ConnectionString;
        await using var connection = new NpgsqlConnection(eeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var processingRunId = (long)(await ScalarCommandAsync(connection, transaction,
            "INSERT INTO ee_processing_runs(delivery_id, raw_message_id, status, started_at) VALUES (@delivery, @raw, 'processing', now()) RETURNING processing_run_id",
            new NpgsqlParameter("delivery", deliveryId), new NpgsqlParameter("raw", rawMessageId)))!;
        var extractorId = (long)(await ScalarCommandAsync(connection, transaction,
            "SELECT extractor_id FROM ee_extractors ORDER BY extractor_id LIMIT 1"))!;
        var extractorRunId = (long)(await ScalarCommandAsync(connection, transaction,
            "INSERT INTO ee_extractor_runs(processing_run_id, extractor_id, status, writes_count, duration_ms) VALUES (@run, @extractor, 'completed', 1, 1) RETURNING extractor_run_id",
            new NpgsqlParameter("run", processingRunId), new NpgsqlParameter("extractor", extractorId)))!;
        var targetId = (long)(await ScalarCommandAsync(connection, transaction,
            "INSERT INTO ee_targets(raw_message_id, label) VALUES (@raw, 'probe') RETURNING \"targetId\"",
            new NpgsqlParameter("raw", rawMessageId)))!;
        var definitionId = (long)(await ScalarCommandAsync(connection, transaction,
            "SELECT entity_definition_id FROM ee_entity_definitions WHERE table_name='ee_targets'"))!;

        await NonQueryCommandAsync(connection, transaction,
            "INSERT INTO ee_entity_writes(processing_run_id, extractor_run_id, entity_definition_id, table_name, entity_id, created_at) VALUES (@run, @extractorRun, @definition, 'ee_targets', @entity, now())",
            new NpgsqlParameter("run", processingRunId), new NpgsqlParameter("extractorRun", extractorRunId),
            new NpgsqlParameter("definition", definitionId), new NpgsqlParameter("entity", targetId));
        var llmRequestId = (long)(await ScalarCommandAsync(connection, transaction,
            "INSERT INTO llm_requests(raw_message_id, source_id, occurred_at, worker, model, prompt_version, outcome, duration_ms, facts_count, request_text, system_prompt) VALUES (@raw, @source, now(), 'entity-extractor', 'probe', 'probe', 'started', 1, 0, 'request', 'system') RETURNING llm_request_id",
            new NpgsqlParameter("raw", rawMessageId), new NpgsqlParameter("source", sourceId)))!;
        var llmAuditUpdated = (bool)(await ScalarCommandAsync(connection, transaction,
            "SELECT ee_finalize_llm_request(@id, 'empty', 200, 1, 1, 0, 0, 1, 0.0, 0, '{}', '{}'::jsonb, NULL)",
            new NpgsqlParameter("id", llmRequestId)))!;

        Assert.True(processingRunId > 0);
        Assert.True(extractorRunId > 0);
        Assert.True(targetId > 0);
        Assert.True(llmRequestId > 0);
        Assert.True(llmAuditUpdated);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Sender_records_failure_then_continues_to_the_next_message()
    {
        await InsertRawAsync("ee-http-1");
        await InsertRawAsync("ee-http-2");

        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("offline") },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("1") });
        var options = new StaticOptionsMonitor<EntityDeliveryOptions>(new EntityDeliveryOptions
        {
            Url = "http://extractor.test",
            Concurrency = 1,
            PollingInterval = TimeSpan.FromMilliseconds(10),
            DeliveryTimeout = TimeSpan.FromSeconds(2),
            ClaimLease = TimeSpan.FromSeconds(5),
            Token = "delivery-secret",
        });
        var loop = new EntityDeliveryLoop(
            new EntityDeliveryStore(Factory, TimeProvider.System),
            new HttpClient(handler), options, new EntityDeliveryIdentity("integration-test"),
            NullLogger<EntityDeliveryLoop>.Instance);

        await loop.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await using var db = await Factory.CreateDbContextAsync();
                if (await db.EntityDeliveries.CountAsync(x => x.CompletedAt != null) == 2)
                {
                    break;
                }
                await Task.Delay(25);
            }
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
            loop.Dispose();
        }

        await using (var db = await Factory.CreateDbContextAsync())
        {
            var deliveries = await db.EntityDeliveries.OrderBy(x => x.EnqueuedAt).ToListAsync();
            Assert.Equal(["failed", "succeeded"], deliveries.Select(x => x.Status).ToArray());
            Assert.All(deliveries, x => Assert.Equal(1, x.Attempts));
            Assert.Equal(2, await db.EntityDeliveryAttempts.CountAsync());
            Assert.Contains("HTTP 503", deliveries[0].LastError);
            Assert.Equal((short)1, deliveries[1].Result);
        }
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, x => Assert.True(x.HasIdempotencyKey));
        Assert.All(handler.Requests, x => Assert.True(x.HasServiceToken));
    }

    [Fact]
    public async Task Expired_lease_reuses_delivery_id_but_creates_a_new_attempt()
    {
        await InsertRawAsync("ee-lease");
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var store = new EntityDeliveryStore(Factory, clock);
        var first = await store.ClaimNextAsync("worker-a", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(first);
        clock.Advance(TimeSpan.FromSeconds(6));
        var recovered = await store.ClaimNextAsync("worker-a", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal(first.Request.DeliveryId, recovered.Request.DeliveryId);
        Assert.NotEqual(first.AttemptId, recovered.AttemptId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(
            first, "worker-a", "succeeded", 200, 1, null, TimeSpan.FromMilliseconds(1), CancellationToken.None));
        await store.CompleteAsync(
            recovered, "worker-a", "succeeded", 200, 1, null, TimeSpan.FromMilliseconds(1), CancellationToken.None);
        await using var db = await Factory.CreateDbContextAsync();
        var delivery = await db.EntityDeliveries.SingleAsync(x => x.DeliveryId == recovered.Request.DeliveryId);
        Assert.Equal("succeeded", delivery.Status);
        Assert.Equal(2, delivery.Attempts);
    }

    [Fact]
    public async Task Concurrent_claims_split_the_queue_and_empty_polls_do_not_throw()
    {
        for (var i = 0; i < 3; i++)
        {
            await InsertRawAsync($"ee-concurrent-{i}");
        }
        var store = new EntityDeliveryStore(Factory, TimeProvider.System);

        // Eight claimers over three rows: three win a delivery, the rest poll an empty queue, all at once.
        for (var round = 0; round < 3; round++)
        {
            var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
                store.ClaimNextAsync($"worker-{i}", TimeSpan.FromMinutes(1), CancellationToken.None)));
            var won = claims.Select((claim, i) => (claim, owner: $"worker-{i}")).Where(x => x.claim is not null).ToArray();
            Assert.Equal(round == 0 ? 3 : 0, won.Length);
            Assert.Equal(won.Length, won.Select(x => x.claim!.Request.DeliveryId).Distinct().Count());
            await Task.WhenAll(won.Select(x => store.CompleteAsync(
                x.claim!, x.owner, "succeeded", 200, 1, null, TimeSpan.FromMilliseconds(1), CancellationToken.None)));
        }

        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(3, await db.EntityDeliveries.CountAsync(x => x.Status == "succeeded" && x.Attempts == 1));
        Assert.Equal(3, await db.EntityDeliveryAttempts.CountAsync(x => x.Outcome == "succeeded"));
    }

    private async Task InsertRawAsync(string key)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var sourceId = await db.Sources.Select(x => x.SourceId).FirstAsync();
        db.RawMessages.Add(Raw(sourceId, key));
        await db.SaveChangesAsync();
    }

    private static RawMessage Raw(int sourceId, string key) => new()
    {
        SourceId = sourceId,
        SourceMessageId = key,
        SourceMessageKey = key,
        SourceRevision = "0",
        PublishedAt = DateTimeOffset.UtcNow,
        ReceivedAt = DateTimeOffset.UtcNow,
        RawText = "test entity message",
        RawPayload = JsonDocument.Parse("{\"kind\":\"test\"}"),
        Url = "https://example.test/message",
        Hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))),
    };

    private static async Task<int> ScalarAsync(PulujDbContext db, string sql, params object[] parameters)
    {
        var value = await db.Database.SqlQueryRaw<int>(sql, parameters).SingleAsync();
        return value;
    }

    private static Task<bool> TableSelectPrivilegeAsync(PulujDbContext db, string table) =>
        TablePrivilegeAsync(db, table, "SELECT", "puluj_reader");

    private static Task<bool> TablePrivilegeAsync(PulujDbContext db, string table, string privilege, string role) =>
        db.Database.SqlQueryRaw<bool>(
            "SELECT has_table_privilege({0}, {1}, {2}) AS \"Value\"", role, table, privilege).SingleAsync();

    private static async Task<object?> ScalarCommandAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<int> NonQueryCommandAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<HttpResponseMessage> _responses = new(responses);
        public ConcurrentBag<(Guid DeliveryId, bool HasIdempotencyKey, bool HasServiceToken)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var deliveryId = JsonDocument.Parse(body).RootElement.GetProperty("deliveryId").GetGuid();
            Requests.Add((
                deliveryId,
                request.Headers.Contains("Idempotency-Key"),
                request.Headers.TryGetValues("X-Entity-Extractor-Token", out var values)
                    && values.SingleOrDefault() == "delivery-secret"));
            return _responses.TryDequeue(out var response)
                ? response
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("0") };
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }
}
