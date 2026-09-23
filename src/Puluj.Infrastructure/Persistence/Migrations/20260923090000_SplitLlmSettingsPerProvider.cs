using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations;

/// <summary>
/// The LLM settings become one block with a section per provider (Llm:Anthropic:*, Llm:OpenAI:*, Llm:Ollama:*), so every
/// provider keeps its own key, model, endpoint and price card and Llm:Provider only switches between them. The flat keys
/// of the single-provider era (Llm:ApiKey, Llm:Model, Llm:BaseUrl, Llm:*UsdPerMillionTokens) move to the section of the
/// provider they were serving — Anthropic unless Llm:Provider says otherwise; a value already set there wins.
/// app_settings is a handful of rows: no lock worth shaping.
/// </summary>
[DbContext(typeof(PulujDbContext))]
[Migration("20260923090000_SplitLlmSettingsPerProvider")]
public partial class SplitLlmSettingsPerProvider : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        WITH provider AS (
            SELECT CASE lower(coalesce((SELECT btrim(value) FROM app_settings WHERE key = 'Llm:Provider'), ''))
                       WHEN 'openai' THEN 'OpenAI'
                       WHEN 'ollama' THEN 'Ollama'
                       ELSE 'Anthropic'
                   END AS name
        )
        INSERT INTO app_settings (key, value, is_secret, updated_at)
        SELECT 'Llm:' || provider.name || ':' || substr(legacy.key, 5), legacy.value, legacy.is_secret, now()
        FROM app_settings AS legacy CROSS JOIN provider
        WHERE legacy.key IN ('Llm:ApiKey', 'Llm:Model', 'Llm:BaseUrl', 'Llm:InputUsdPerMillionTokens', 'Llm:OutputUsdPerMillionTokens',
                             'Llm:CacheWriteUsdPerMillionTokens', 'Llm:CacheReadUsdPerMillionTokens')
          AND NOT (provider.name = 'Anthropic' AND legacy.key = 'Llm:BaseUrl')
          AND NOT (provider.name = 'Ollama' AND legacy.key <> 'Llm:Model' AND legacy.key <> 'Llm:BaseUrl')
        ON CONFLICT (key) DO NOTHING;

        DELETE FROM app_settings
        WHERE key IN ('Llm:ApiKey', 'Llm:Model', 'Llm:BaseUrl', 'Llm:InputUsdPerMillionTokens', 'Llm:OutputUsdPerMillionTokens',
                      'Llm:CacheWriteUsdPerMillionTokens', 'Llm:CacheReadUsdPerMillionTokens');
        """);

    // The flat keys are not restored: the per-provider rows stay, and the previous code falls back to its defaults.
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
