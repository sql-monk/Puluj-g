using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml;
using Npgsql;
using Puluj.Admin;

namespace Puluj.EntityAdmin;

public sealed partial class EntityAdminStore(IConfiguration configuration, Puluj.Infrastructure.Settings.SettingsStore settings)
{
    private readonly string connectionString = configuration.GetConnectionString(Puluj.Infrastructure.DependencyInjection.ConnectionStringName)
        ?? throw new InvalidOperationException("ConnectionStrings:Puluj is required.");
    private static readonly HashSet<string> Types = ["text", "integer", "decimal", "boolean", "datetime", "json", "point", "line", "polygon"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Queue counters scan the matching statuses. Latest success uses the partial completion-time index;
    /// enqueue order is not completion order when deliveries run concurrently or a lease is recovered.
    /// </summary>
    public async Task<EeQueueSnapshot> QueueSnapshotAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM ee_delivery_queue WHERE status = 'pending'),
              (SELECT count(*) FROM ee_delivery_queue WHERE status = 'in_progress'),
              (SELECT count(*) FROM ee_delivery_queue WHERE status = 'failed'),
              (SELECT count(*) FROM ee_delivery_queue WHERE status = 'failed' AND completed_at >= now() - interval '1 hour'),
              (SELECT count(*) FROM ee_delivery_queue WHERE status = 'failed' AND completed_at >= now() - interval '24 hours'),
              (SELECT max(completed_at) FROM ee_delivery_queue WHERE status = 'failed'),
              (SELECT last_error FROM ee_delivery_queue WHERE status = 'failed' ORDER BY completed_at DESC NULLS LAST LIMIT 1),
              (SELECT min(enqueued_at) FROM ee_delivery_queue WHERE status = 'pending'),
              (SELECT completed_at FROM ee_delivery_queue WHERE status = 'succeeded' AND completed_at IS NOT NULL ORDER BY completed_at DESC LIMIT 1)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        DateTimeOffset? At(int i) => reader.IsDBNull(i) ? null : reader.GetFieldValue<DateTimeOffset>(i);
        return new EeQueueSnapshot(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
            At(5), reader.IsDBNull(6) ? null : reader.GetString(6), At(7), At(8));
    }

    public async Task<(long Extractors, long Definitions, long ActiveRuns)> CountsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM ee_extractors WHERE enabled),
                   (SELECT count(*) FROM ee_entity_definitions WHERE enabled),
                   (SELECT count(*) FROM ee_delivery_queue WHERE status = 'in_progress')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    public async Task<IReadOnlyList<EeSettingDto>> SettingsAsync(CancellationToken ct)
    {
        var stored = await settings.GetAllAsync(ct);
        return EntityExtractorSettings.Describe(
            EntityExtractorSettings.Keys.Where(stored.ContainsKey).ToDictionary(key => key, key => stored[key].Value),
            // IConfiguration includes a cached DB provider: after deleting an override it can still return the deleted value.
            key => EntityExtractorSettings.ConfigurationFallback(configuration, key));
    }

    public Task SaveSettingsAsync(IReadOnlyDictionary<string, string?> values, CancellationToken ct) =>
        settings.SetAsync(values.ToDictionary(x => x.Key, x => string.IsNullOrWhiteSpace(x.Value) ? null : x.Value.Trim()), ct);

    public Task<IReadOnlyList<JsonElement>> ExtractorsAsync(CancellationToken ct) => QueryRowsAsync("SELECT * FROM ee_extractors ORDER BY execution_order, extractor_id", ct);
    public Task<IReadOnlyList<JsonElement>> DefinitionsAsync(CancellationToken ct) => QueryRowsAsync("SELECT * FROM ee_entity_definitions ORDER BY entity_name", ct);

    private const string DeliveryColumns = """
        q.delivery_id, q.raw_message_id, r.source_id, s.code AS source_code, q.origin, q.status, q.enqueued_at, q.claimed_at,
        q.completed_at, q.claimed_by, q.result, q.attempts, q.last_error, pr.error AS run_error
        """;

    /// <summary>
    /// Deliveries newest first, optionally of one status, as keyset pages (cursor = enqueued_at ticks + delivery id). Each status
    /// is read over its (status, enqueued_at) index; "all statuses" merges the four index reads instead of sorting the table.
    /// </summary>
    public async Task<EeDeliveryPageDto> DeliveriesAsync(string? status, string? cursor, int limit, CancellationToken ct)
    {
        var hasCursor = TryParseDeliveryCursor(cursor, out var beforeAt, out var beforeId);
        // `status` is one of the four known values (checked by the endpoint), so it is safe to inline as a literal.
        string Branch(string s) => $"""
            (SELECT q.* FROM ee_delivery_queue q
             WHERE q.status = '{s}'{(hasCursor ? " AND q.enqueued_at <= @before_at AND (q.enqueued_at < @before_at OR q.delivery_id < @before_id)" : "")}
             ORDER BY q.enqueued_at DESC, q.delivery_id DESC
             LIMIT @take)
            """;
        var statuses = status is null ? ["pending", "in_progress", "succeeded", "failed"] : new[] { status };
        var union = string.Join("\nUNION ALL\n", statuses.Select(Branch));
        var sql = $"""
            SELECT {DeliveryColumns}
            FROM ({union}) q
            JOIN raw_messages r ON r.raw_message_id = q.raw_message_id
            JOIN sources s ON s.source_id = r.source_id
            LEFT JOIN ee_processing_runs pr ON pr.delivery_id = q.delivery_id
            ORDER BY q.enqueued_at DESC, q.delivery_id DESC
            LIMIT @take
            """;
        var parameters = new List<NpgsqlParameter> { new("take", limit + 1) };
        if (hasCursor)
        {
            parameters.Add(new NpgsqlParameter("before_at", beforeAt));
            parameters.Add(new NpgsqlParameter("before_id", beforeId));
        }
        var rows = await QueryRowsAsync(sql, ct, [.. parameters]);
        var page = rows.Take(limit).ToList();
        string? next = null;
        if (rows.Count > limit)
        {
            var last = page[^1];
            next = $"{last.GetProperty("enqueued_at").GetDateTimeOffset().UtcTicks}_{last.GetProperty("delivery_id").GetGuid():N}";
        }
        return new EeDeliveryPageDto(page, next);
    }

    /// <summary>Processing runs of the extractor, newest first, over the primary key (the table has millions of rows and no time index).</summary>
    public async Task<EeRunPageDto> RunsAsync(long? beforeId, int limit, CancellationToken ct)
    {
        var sql = $"""
            SELECT pr.processing_run_id, pr.delivery_id, pr.raw_message_id, r.source_id, s.code AS source_code, pr.status, pr.result,
                   pr.started_at, pr.completed_at, pr.error
            FROM ee_processing_runs pr
            JOIN raw_messages r ON r.raw_message_id = pr.raw_message_id
            JOIN sources s ON s.source_id = r.source_id
            {(beforeId is null ? "" : "WHERE pr.processing_run_id < @before_id")}
            ORDER BY pr.processing_run_id DESC
            LIMIT @take
            """;
        var parameters = new List<NpgsqlParameter> { new("take", limit + 1) };
        if (beforeId is { } before) parameters.Add(new NpgsqlParameter("before_id", before));
        var rows = await QueryRowsAsync(sql, ct, [.. parameters]);
        var page = rows.Take(limit).ToList();
        return new EeRunPageDto(page, rows.Count > limit ? page[^1].GetProperty("processing_run_id").GetInt64() : null);
    }

    private static bool TryParseDeliveryCursor(string? cursor, out DateTimeOffset at, out Guid id)
    {
        at = default;
        id = default;
        var parts = cursor?.Split('_');
        if (parts is not { Length: 2 } || !long.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTimeOffset.MaxValue.UtcTicks || !Guid.TryParseExact(parts[1], "N", out id))
        {
            return false;
        }
        at = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

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

public sealed record EeDeliveryPageDto(IReadOnlyList<JsonElement> Items, string? NextCursor);
public sealed record EeRunPageDto(IReadOnlyList<JsonElement> Items, long? NextBeforeId);
