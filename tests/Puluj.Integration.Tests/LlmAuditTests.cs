using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;
using Puluj.Infrastructure.Ingestion;
using Puluj.Infrastructure.Persistence;
using Puluj.Processing.Parsing;
using Puluj.Processing.Pipeline;
using Puluj.Processing.Text;

namespace Puluj.Integration.Tests;

/// <summary>
/// The LLM audit row is written from inside the parse, through its own connection, while the processor's transaction
/// holds the raw row. Its FK to raw_messages needs KEY SHARE on that row: with the processor holding FOR UPDATE the
/// insert waited for a transaction that was itself waiting for the parse, and every audit row died on the command
/// timeout (llm_requests stayed empty). FOR NO KEY UPDATE keeps the fence and lets the audit through.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class LlmAuditTests(PipelineFixture fixture) : IAsyncLifetime
{
    private ServiceProvider Services => fixture.Services ?? throw new InvalidOperationException("PostGIS required");
    private IDbContextFactory<PulujDbContext> Factory => Services.GetRequiredService<IDbContextFactory<PulujDbContext>>();
    private static readonly DateTimeOffset At = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDataAsync();
    public Task DisposeAsync() => fixture.ResetDataAsync();

    [Fact]
    public async Task Audit_row_is_written_while_the_processor_holds_the_message()
    {
        var id = await Text("llm-audit", "Шахеди на Сумщині курсом на Полтавщину.");
        var parser = new AuditingParser(Factory, Services.GetRequiredService<RuleParser>());
        var processor = ActivatorUtilities.CreateInstance<RawMessageProcessor>(Services, new ProcessorIdentity("llm"), (IParser)parser);

        Assert.Equal(1, await processor.ProcessAsync(id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Null(parser.AuditError);
        await using var db = await Factory.CreateDbContextAsync();
        var audit = await db.LlmRequests.AsNoTracking().SingleAsync(r => r.RawMessageId == id);
        Assert.Equal("facts", audit.Outcome);
        Assert.Equal(ProcessingStatus.Processed, (await db.RawMessages.AsNoTracking().SingleAsync(r => r.RawMessageId == id)).ProcessingStatus);
    }

    /// <summary>Does what LlmParser.AuditAsync does — a separate context, an insert referencing the message being parsed — with a short timeout so a blocked FK check shows up as a failure, not a 30 s stall.</summary>
    private sealed class AuditingParser(IDbContextFactory<PulujDbContext> factory, IParser inner) : IParser
    {
        public Exception? AuditError { get; private set; }

        public async Task<IReadOnlyList<ParsedFact>> ParseAsync(NormalizedMessage message, ParseContext context, CancellationToken ct)
        {
            var facts = await inner.ParseAsync(message, context, ct);
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                db.Database.SetCommandTimeout(TimeSpan.FromSeconds(5));
                db.LlmRequests.Add(new LlmRequest
                {
                    RawMessageId = context.RawMessageId,
                    SourceId = context.SourceId,
                    OccurredAt = DateTimeOffset.UtcNow,
                    Worker = "llm",
                    Model = "test",
                    PromptVersion = "test",
                    Outcome = "facts",
                    DurationMs = 1,
                    FactsCount = facts.Count,
                    RequestText = message.Text,
                    SystemPrompt = "test",
                    ResponseText = "{}",
                });
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                AuditError = ex;
            }
            return facts;
        }
    }

    private async Task<long> Text(string key, string text, string source = "tg_kpszsu")
    {
        await using var db = await Factory.CreateDbContextAsync();
        var sourceId = await db.Sources.Where(s => s.Code == source).Select(s => s.SourceId).SingleAsync();
        var result = await Services.GetRequiredService<RawMessageIngestor>().IngestAsync(new()
        {
            SourceId = sourceId, SourceMessageId = key, RawText = text,
            RawPayload = JsonSerializer.SerializeToDocument(new { test = key }), PublishedAt = At,
        }, source, CancellationToken.None, announceProcessor: false);
        Assert.True(result.IsNew);
        return result.RawMessageId!.Value;
    }
}
