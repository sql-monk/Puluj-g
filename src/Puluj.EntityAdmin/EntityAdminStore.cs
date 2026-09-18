using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml;
using Npgsql;

namespace Puluj.EntityAdmin;

public sealed partial class EntityAdminStore(IConfiguration configuration, Puluj.Infrastructure.Settings.SettingsStore settings)
{
    private readonly string connectionString = configuration.GetConnectionString(Puluj.Infrastructure.DependencyInjection.ConnectionStringName)
        ?? throw new InvalidOperationException("ConnectionStrings:Puluj is required.");
    private static readonly HashSet<string> Types = ["text", "integer", "decimal", "boolean", "datetime", "json", "point", "line", "polygon"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<JsonElement> OverviewAsync(CancellationToken ct) => QueryObjectAsync("""
        SELECT jsonb_build_object(
          'queued', (SELECT count(*) FROM ee_delivery_queue WHERE status = 'pending'),
          'failed', (SELECT count(*) FROM ee_delivery_queue WHERE status = 'failed'),
          'extractors', (SELECT count(*) FROM ee_extractors WHERE enabled),
          'definitions', (SELECT count(*) FROM ee_entity_definitions WHERE enabled),
          'activeRuns', (SELECT count(*) FROM ee_processing_runs WHERE status = 'processing'),
          'oldestQueuedAt', (SELECT min(enqueued_at) FROM ee_delivery_queue WHERE status = 'pending'),
          'oldestQueueAgeSeconds', (SELECT extract(epoch FROM now() - min(enqueued_at))::bigint FROM ee_delivery_queue WHERE status = 'pending'),
          'lastError', (SELECT error FROM ee_delivery_attempts WHERE error IS NOT NULL ORDER BY started_at DESC LIMIT 1)
        )
        """, ct);

    private static readonly string[] SettingKeys = ["EntityExtractor:Url", "EntityExtractor:DeliveryTimeout", "EntityExtractor:PollingInterval", "EntityExtractor:ClaimLease", "EntityExtractor:Concurrency"];
    public async Task<IReadOnlyDictionary<string, string?>> SettingsAsync(CancellationToken ct)
    {
        var stored = await settings.GetAllAsync(ct);
        return SettingKeys.ToDictionary(key => key, key => stored.TryGetValue(key, out var row) ? row.Value : configuration[key], StringComparer.OrdinalIgnoreCase);
    }
    public async Task SaveSettingsAsync(IReadOnlyDictionary<string, string?> values, CancellationToken ct)
    {
        if (values.Keys.Any(key => !SettingKeys.Contains(key, StringComparer.OrdinalIgnoreCase))) throw new ArgumentException("Only EntityExtractor settings can be changed here.");
        if (values.TryGetValue("EntityExtractor:Url", out var url) && !string.IsNullOrWhiteSpace(url) && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) throw new ArgumentException("EntityExtractor:Url must be an absolute HTTP(S) URL.");
        if (values.TryGetValue("EntityExtractor:Concurrency", out var concurrency) && !string.IsNullOrWhiteSpace(concurrency) && (!int.TryParse(concurrency, out var workers) || workers is < 1 or > 128)) throw new ArgumentException("EntityExtractor:Concurrency must be between 1 and 128.");
        await settings.SetAsync(values, ct);
    }

    public Task<IReadOnlyList<JsonElement>> ExtractorsAsync(CancellationToken ct) => QueryRowsAsync("SELECT * FROM ee_extractors ORDER BY execution_order, extractor_id", ct);
    public Task<IReadOnlyList<JsonElement>> DefinitionsAsync(CancellationToken ct) => QueryRowsAsync("SELECT * FROM ee_entity_definitions ORDER BY entity_name", ct);
    public Task<IReadOnlyList<JsonElement>> DeliveriesAsync(int limit, CancellationToken ct) => QueryRowsAsync("SELECT * FROM ee_delivery_queue ORDER BY enqueued_at DESC LIMIT @limit", ct, new NpgsqlParameter("limit", limit));
    public Task<IReadOnlyList<JsonElement>> RunsAsync(int limit, CancellationToken ct) => QueryRowsAsync("SELECT * FROM ee_processing_runs ORDER BY started_at DESC LIMIT @limit", ct, new NpgsqlParameter("limit", limit));

    public async Task<JsonElement> SaveExtractorAsync(ExtractorSaveRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120) throw new ArgumentException("Extractor name is required and must be at most 120 characters.");
        if (string.IsNullOrWhiteSpace(request.Code)) throw new ArgumentException("Python code is required.");
        if (request.TimeoutMs is < 100 or > 120_000) throw new ArgumentException("Timeout must be between 100 and 120000 ms.");
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = request.ExtractorId is null
            ? "INSERT INTO ee_extractors(name, code, enabled, execution_order, timeout_ms) VALUES (@name,@code,@enabled,@order,@timeout) RETURNING row_to_json(ee_extractors)::text"
            : "UPDATE ee_extractors SET name=@name,code=@code,enabled=@enabled,execution_order=@order,timeout_ms=@timeout WHERE extractor_id=@id RETURNING row_to_json(ee_extractors)::text";
        command.Parameters.AddWithValue("name", request.Name.Trim()); command.Parameters.AddWithValue("code", request.Code); command.Parameters.AddWithValue("enabled", request.Enabled); command.Parameters.AddWithValue("order", request.ExecutionOrder); command.Parameters.AddWithValue("timeout", request.TimeoutMs);
        if (request.ExtractorId is { } id) command.Parameters.AddWithValue("id", id);
        var json = (string?)await command.ExecuteScalarAsync(ct) ?? throw new KeyNotFoundException("Extractor was not found.");
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    public async Task DeleteExtractorAsync(long id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("DELETE FROM ee_extractors WHERE extractor_id=@id", connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<JsonElement> CreateDefinitionAsync(EntityDefinitionSaveRequest request, CancellationToken ct)
    {
        var entity = NormalizeEntityName(request.EntityName);
        if (request.Fields.Count == 0) throw new ArgumentException("At least one field is required.");
        if (request.Fields.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Fields.Count) throw new ArgumentException("Field names must be unique.");
        var reserved = new[] { "rawMessageId", entity + "Id", "raw_message_id", ToSnakeCase(entity) + "_id" };
        if (request.Fields.Any(x => reserved.Contains(x.Name, StringComparer.OrdinalIgnoreCase))) throw new ArgumentException("ID and rawMessageId fields are added automatically and cannot be declared.");
        foreach (var field in request.Fields) { ValidateIdentifier(field.Name); if (!Types.Contains(field.Type)) throw new ArgumentException($"Unsupported field type '{field.Type}'."); }
        var map = request.Map with { Svg = SanitizeSvg(request.Map.Svg) };
        ValidateMap(map, request.Fields);
        var fieldsJson = JsonSerializer.Serialize(request.Fields, JsonOptions);
        var mapJson = JsonSerializer.Serialize(map, JsonOptions);
        await using var connection = await OpenAsync(ct);
        await using var insert = new NpgsqlCommand("SELECT ee_create_entity_definition(@entity, CAST(@fields AS jsonb), CAST(@map AS jsonb), @enabled)::text", connection);
        insert.Parameters.AddWithValue("entity", entity); insert.Parameters.AddWithValue("fields", fieldsJson); insert.Parameters.AddWithValue("map", mapJson); insert.Parameters.AddWithValue("enabled", request.Enabled);
        var json = (string?)await insert.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Definition was not created.");
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    public async Task UpdateMapConfigAsync(long id, MapConfigRequest request, CancellationToken ct)
    {
        var sanitized = request with { Svg = SanitizeSvg(request.Svg) };
        await using var connection = await OpenAsync(ct);
        await using (var read = new NpgsqlCommand("SELECT fields::text FROM ee_entity_definitions WHERE entity_definition_id=@id", connection))
        {
            read.Parameters.AddWithValue("id", id);
            var fieldsJson = (string?)await read.ExecuteScalarAsync(ct) ?? throw new KeyNotFoundException("Entity definition was not found.");
            var fields = JsonSerializer.Deserialize<List<EntityFieldRequest>>(fieldsJson, JsonOptions) ?? [];
            ValidateMap(sanitized, fields);
        }
        await using var command = new NpgsqlCommand("UPDATE ee_entity_definitions SET map_settings=CAST(@map AS jsonb), updated_at=now() WHERE entity_definition_id=@id", connection);
        command.Parameters.AddWithValue("map", JsonSerializer.Serialize(sanitized, JsonOptions)); command.Parameters.AddWithValue("id", id);
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw new KeyNotFoundException("Entity definition was not found.");
    }

    public async Task<JsonElement> EnqueueAsync(long rawMessageId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT to_jsonb(ee_enqueue_raw_message(@id))::text", connection);
        command.Parameters.AddWithValue("id", rawMessageId);
        var value = (string?)await command.ExecuteScalarAsync(ct);
        return value is null ? JsonSerializer.SerializeToElement(new { queued = false }) : JsonSerializer.Deserialize<JsonElement>(value);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) { var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct); return connection; }
    private async Task<IReadOnlyList<JsonElement>> QueryRowsAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var connection = await OpenAsync(ct); await using var command = new NpgsqlCommand($"SELECT row_to_json(q)::text FROM ({sql}) q", connection); command.Parameters.AddRange(parameters);
        var rows = new List<JsonElement>(); await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) rows.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0))); return rows;
    }
    private async Task<JsonElement> QueryObjectAsync(string sql, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); await using var command = new NpgsqlCommand($"SELECT ({sql})::text", connection); var json = (string?)await command.ExecuteScalarAsync(ct) ?? "{}"; return JsonSerializer.Deserialize<JsonElement>(json);
    }
    private static void ValidateMap(MapConfigRequest map, IReadOnlyList<EntityFieldRequest> fields)
    {
        var byName = fields.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var names = byName.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { map.LabelField, map.TimeField, map.StatusField, map.GeometryField, map.LatitudeField, map.LongitudeField }.Where(x => !string.IsNullOrWhiteSpace(x))) if (!names.Contains(name!)) throw new ArgumentException($"Map field '{name}' is not defined.");
        if (map.TimeField is { Length: > 0 } time && !byName[time].Type.Equals("datetime", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Map time field must have datetime type.");
        if (map.GeometryField is { Length: > 0 } geometry && byName[geometry].Type is not ("point" or "line" or "polygon")) throw new ArgumentException("Map geometry field must have point, line, or polygon type.");
        foreach (var coordinate in new[] { map.LatitudeField, map.LongitudeField }.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (byName[coordinate!].Type is not ("integer" or "decimal")) throw new ArgumentException($"Map coordinate field '{coordinate}' must have integer or decimal type.");
        if (map.Renderer is not ("point" or "icon" or "line" or "polygon")) throw new ArgumentException("Renderer must be point, icon, line, or polygon.");
        if (map.LifetimeMinutes is <= 0) throw new ArgumentException("Lifetime must be greater than zero.");
        if (map.Width is < 0 or > 100) throw new ArgumentException("Width must be between 0 and 100.");
        if (map.Opacity is < 0 or > 1) throw new ArgumentException("Opacity must be between 0 and 1.");
        if (map.Dash is not null && map.Dash is not ("dashed" or "dotted")) throw new ArgumentException("Dash must be dashed or dotted.");
    }
    private static string? SanitizeSvg(string? svg)
    {
        if (string.IsNullOrWhiteSpace(svg)) return null;
        if (svg.Length > 64_000) throw new ArgumentException("SVG is too large.");
        using var stringReader = new StringReader(svg);
        using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(xmlReader, LoadOptions.None);
        if (document.Root?.Name.LocalName != "svg") throw new ArgumentException("The icon must be an SVG document.");
        var dangerous = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "script", "style", "foreignObject", "iframe", "object", "embed", "use", "image" };
        foreach (var element in document.Descendants().Where(x => dangerous.Contains(x.Name.LocalName)).ToArray()) element.Remove();
        foreach (var attribute in document.Root.DescendantsAndSelf().Attributes().Where(x =>
            x.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
            || x.Name.LocalName is "href"
            || (!string.IsNullOrEmpty(x.Name.NamespaceName) && !x.IsNamespaceDeclaration)
            || (x.IsNamespaceDeclaration && x.Name.LocalName != "xmlns")
            || x.Value.Contains("url(", StringComparison.OrdinalIgnoreCase)
            || x.Value.Contains("@import", StringComparison.OrdinalIgnoreCase)
            || x.Value.Contains("expression(", StringComparison.OrdinalIgnoreCase)
            || x.Value.Contains("javascript:", StringComparison.OrdinalIgnoreCase)).ToArray()) attribute.Remove();
        return document.ToString(SaveOptions.DisableFormatting);
    }
    private static string NormalizeEntityName(string name) { var trimmed = name.Trim(); ValidateIdentifier(trimmed); return char.ToLowerInvariant(trimmed[0]) + trimmed[1..]; }
    private static string ToSnakeCase(string value) => Regex.Replace(value, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();
    private static void ValidateIdentifier(string value) { if (!Identifier().IsMatch(value)) throw new ArgumentException($"Invalid identifier '{value}'."); }
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant)] private static partial Regex Identifier();
}
