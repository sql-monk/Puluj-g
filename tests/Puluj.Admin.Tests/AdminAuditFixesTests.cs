using Npgsql;
using Microsoft.Extensions.Configuration;
using Puluj.Admin;
using Puluj.Admin.Endpoints;
using Puluj.Infrastructure.Settings;

namespace Puluj.Admin.Tests;

/// <summary>The pure parts of the admin UI audit fixes (docs/audits/admin-ui-audit-2026-09-24.md): A04, A05, A09 and the page cursors.</summary>
public sealed class AdminAuditFixesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A09_syntax_error_keeps_postgres_message_state_and_position()
    {
        var error = DatabaseQueryErrors.Describe(new PostgresException("syntax error at end of input", "ERROR", "ERROR", "42601", position: 11));

        Assert.NotNull(error);
        Assert.Equal("Помилка SQL: syntax error at end of input", error.Error);
        Assert.Equal("42601", error.SqlState);
        Assert.Equal(11, error.Position);
    }

    [Fact]
    public void A09_statement_timeout_is_explained_as_the_10_second_limit()
    {
        var error = DatabaseQueryErrors.Describe(new PostgresException("canceling statement due to statement timeout", "ERROR", "ERROR", PostgresErrorCodes.QueryCanceled));

        Assert.NotNull(error);
        Assert.Contains("10 с", error.Error);
        Assert.Null(error.Position);
    }

    [Fact]
    public void A09_non_query_failures_stay_internal()
    {
        Assert.Null(DatabaseQueryErrors.Describe(new InvalidOperationException("connection string: Password=secret")));
    }

    [Fact]
    public void A04_historic_failures_do_not_make_a_healthy_extractor_red()
    {
        var queue = new EeQueueSnapshot(0, 0, 19_137, 0, 0, Now.AddDays(-2), "HTTP 500: LLM circuit breaker is paused", null, Now.AddSeconds(-5));

        var status = EntityExtractorStatus.Describe(new EeProbe(true, 200, null), queue, llmEnabled: false, Now);

        Assert.Equal("entity-extractor", status.Name);
        Assert.Equal("ok", status.Status);
        Assert.Contains("0 за годину", status.Detail);
        Assert.Contains("остання 2 дн тому", status.Detail);
        Assert.Contains("LLM вимкнено навмисно", status.Detail);
        Assert.Equal(Now.AddSeconds(-5), status.LastSeen);
    }

    [Fact]
    public void A04_recent_failures_warn_and_unreachable_is_down()
    {
        var failing = new EeQueueSnapshot(10, 1, 50, 3, 20, Now.AddMinutes(-2), "HTTP 500", Now.AddMinutes(-1), Now);
        Assert.Equal("warn", EntityExtractorStatus.Describe(new EeProbe(true, 200, null), failing, true, Now).Status);

        var down = EntityExtractorStatus.Describe(new EeProbe(false, null, "connection refused"), failing, true, Now);
        Assert.Equal("down", down.Status);
        Assert.Contains("connection refused", down.Detail);
    }

    [Fact]
    public void A04_a_queue_that_does_not_move_warns()
    {
        var stuck = new EeQueueSnapshot(500, 0, 0, 0, 0, null, null, Now.AddHours(-3), Now.AddHours(-2));
        Assert.Equal("warn", EntityExtractorStatus.Describe(new EeProbe(true, 200, null), stuck, true, Now).Status);
    }

    [Fact]
    public void A05_settings_show_effective_value_and_its_origin()
    {
        var settings = EntityExtractorSettings.Describe(
            new Dictionary<string, string?> { ["EntityExtractor:Concurrency"] = "8" },
            key => key == "EntityExtractor:Url" ? "http://entity-extractor:8080" : null);

        var concurrency = settings.Single(s => s.Key == "EntityExtractor:Concurrency");
        Assert.Equal(("db", "8", "8", "4"), (concurrency.Source, concurrency.Value, concurrency.Effective, concurrency.Default));
        var url = settings.Single(s => s.Key == "EntityExtractor:Url");
        Assert.Equal(("config", null, "http://entity-extractor:8080"), (url.Source, url.Value, url.Effective));
        var timeout = settings.Single(s => s.Key == "EntityExtractor:DeliveryTimeout");
        Assert.Equal(("default", "00:00:30", "00:00:30"), (timeout.Source, timeout.Effective, timeout.Default));
        Assert.All(settings, s => Assert.False(string.IsNullOrWhiteSpace(s.Label)));
    }

    [Theory]
    [InlineData("EntityExtractor:DeliveryTimeout", "00:00:45", true)]
    [InlineData("EntityExtractor:DeliveryTimeout", "30", false)]
    [InlineData("EntityExtractor:DeliveryTimeout", "01:00:00", false)]
    [InlineData("EntityExtractor:PollingInterval", "00:00:00.500", true)]
    [InlineData("EntityExtractor:ClaimLease", "00:16:00", true)]
    [InlineData("EntityExtractor:ClaimLease", "00:01:00", false)]
    [InlineData("EntityExtractor:ClaimLease", "00:15:00", false)]
    [InlineData("EntityExtractor:ClaimLease", "00:15:01", true)]
    [InlineData("EntityExtractor:ClaimLease", "1.00:00:00", true)]
    [InlineData("EntityExtractor:ClaimLease", "1.00:00:01", false)]
    [InlineData("EntityExtractor:ClaimLease", "abc", false)]
    [InlineData("EntityExtractor:Concurrency", "0", false)]
    [InlineData("EntityExtractor:Concurrency", "16", true)]
    [InlineData("EntityExtractor:Concurrency", "64", true)]
    [InlineData("EntityExtractor:Concurrency", "65", false)]
    [InlineData("EntityExtractor:Concurrency", "128", false)]
    [InlineData("EntityExtractor:Url", "ftp://x", false)]
    [InlineData("EntityExtractor:Url", "", true)]
    [InlineData("Llm:Enabled", "true", false)]
    public void A05_settings_are_validated_before_saving(string key, string value, bool valid)
    {
        var problem = EntityExtractorSettings.Validate(new Dictionary<string, string?> { [key] = value });
        Assert.Equal(valid, problem is null);
    }

    [Theory]
    [InlineData("128", "64")]
    [InlineData("0", "1")]
    public void A05_existing_concurrency_displays_the_worker_limit_without_hiding_the_stored_value(string value, string expected)
    {
        var setting = EntityExtractorSettings.Describe(
            new Dictionary<string, string?> { ["EntityExtractor:Concurrency"] = value }, _ => null)
            .Single(s => s.Key == "EntityExtractor:Concurrency");
        Assert.Equal(value, setting.Value);
        Assert.Equal(expected, setting.Effective);
        Assert.Equal("db", setting.Source);

        var configured = EntityExtractorSettings.Describe(new Dictionary<string, string?>(),
            key => key == "EntityExtractor:Concurrency" ? value : null)
            .Single(s => s.Key == "EntityExtractor:Concurrency");
        Assert.Null(configured.Value);
        Assert.Equal(expected, configured.Effective);
        Assert.Equal("config", configured.Source);
    }

    [Theory]
    [InlineData(true, "config", "16")]
    [InlineData(false, "default", "4")]
    public void A05_deleted_override_ignores_stale_database_provider_and_preserves_fallback_precedence(bool configured, string source, string effective)
    {
        const string key = "EntityExtractor:Concurrency";
        var builder = new ConfigurationBuilder();
        if (configured)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?> { [key] = "8" });
            builder.AddInMemoryCollection(new Dictionary<string, string?> { [key] = "16" });
        }
        // Invalid syntax fails locally before any connection; use the real provider's cache without contacting a database.
        builder.Add(new DbConfigurationSource("invalid connection string", TimeSpan.FromDays(1)));
        using var configuration = (ConfigurationRoot)builder.Build();
        configuration.Providers.OfType<DbConfigurationProvider>().Single().Set(key, "32");
        Assert.Equal("32", configuration[key]);

        // A fresh DB read has no row after deletion, while the five-second configuration cache still has 32.
        var setting = EntityExtractorSettings.Describe(new Dictionary<string, string?>(),
            name => EntityExtractorSettings.ConfigurationFallback(configuration, name)).Single(s => s.Key == key);
        Assert.Null(setting.Value);
        Assert.Equal(source, setting.Source);
        Assert.Equal(effective, setting.Effective);
        Assert.Equal("32", configuration[key]); // Resolving fallback must not mutate the shared provider.
    }

    [Fact]
    public void Message_cursor_round_trips_microsecond_timestamps()
    {
        var at = new DateTimeOffset(2026, 9, 23, 21, 0, 28, TimeSpan.Zero).AddTicks(3_631_230);

        Assert.True(AdminReadQueries.TryParseCursor(AdminReadQueries.Cursor(at, 42), out var parsed, out var id));
        Assert.Equal((at, 42L), (parsed, id));
        Assert.False(AdminReadQueries.TryParseCursor("garbage", out _, out _));
        Assert.False(AdminReadQueries.TryParseCursor(null, out _, out _));
    }
}
