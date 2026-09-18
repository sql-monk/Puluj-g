using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace Puluj.EntityApi;

public sealed partial class EntityQueries(IConfiguration configuration)
{
    public const int MaxCatalogueOffset = 10_000;

    private readonly string connectionString = configuration.GetConnectionString(Puluj.Infrastructure.DependencyInjection.ConnectionStringName)
        ?? throw new InvalidOperationException("ConnectionStrings:Puluj is required.");

    public async Task<IReadOnlyList<EntityDefinitionDto>> DefinitionsAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT entity_name, table_name, fields::text, coalesce(map_settings, '{}'::jsonb)::text, enabled
            FROM ee_entity_definitions
            WHERE enabled
            ORDER BY entity_name
            """;
        var rows = new List<EntityDefinitionDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(ReadDefinition(reader));
        return rows;
    }

    public async Task<EntitySnapshotDto> SnapshotAsync(DateTimeOffset? at, CancellationToken ct)
    {
        const int limitPerEntity = 2000;
        var definitions = await DefinitionsAsync(ct);
        var items = new List<EntityItemDto>();
        var truncated = false;
        foreach (var definition in definitions.Where(x => x.Map.Visible))
        {
            var rows = await ReadRowsAsync(definition, at, limitPerEntity + 1, ct, applyLifetime: true);
            if (rows.Count > limitPerEntity) truncated = true;
            items.AddRange(rows.Take(limitPerEntity));
        }
        return new EntitySnapshotDto(DateTimeOffset.UtcNow, at, items, truncated, limitPerEntity);
    }

    public async Task<EntityPageDto> CatalogueAsync(string? kind, string? kinds, string? query, string? sourceIds, DateTimeOffset? from, DateTimeOffset? to, int limit, int offset, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, MaxCatalogueOffset);
        var definitions = await DefinitionsAsync(ct);
        if (!string.IsNullOrWhiteSpace(kind)) definitions = definitions.Where(x => SameKind(x, kind)).ToArray();
        if (!string.IsNullOrWhiteSpace(kinds))
        {
            var requested = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            definitions = definitions.Where(x => requested.Contains(x.EntityName) || requested.Contains(x.TableName)).ToArray();
        }
        var all = new List<EntityItemDto>();
        var total = 0;
        var perTableLimit = offset + limit;
        var sources = ParseIds(sourceIds);
        foreach (var definition in definitions)
        {
            all.AddRange(await ReadRowsAsync(definition, null, perTableLimit, ct, query, false, sources, from, to));
            total += await CountRowsAsync(definition, query, sources, from, to, ct);
        }
        var ordered = all
            .OrderByDescending(x => x.OccurredAt)
            .ThenBy(x => x.Table, StringComparer.Ordinal)
            .ThenByDescending(x => long.TryParse(x.Id, out var id) ? id : long.MinValue)
            .ToArray();
        var page = ordered.Skip(offset).Take(limit).ToArray();
        var nextOffset = offset + page.Length;
        var nextCursor = nextOffset < total && nextOffset <= MaxCatalogueOffset ? nextOffset.ToString() : null;
        return new EntityPageDto(page, nextCursor, total);
    }

    public async Task<EntityItemDto?> DetailAsync(string kind, string id, CancellationToken ct)
    {
        var definition = (await DefinitionsAsync(ct)).SingleOrDefault(x => SameKind(x, kind));
        if (definition is null || !long.TryParse(id, out var numericId)) return null;
        return await ReadOneAsync(definition, numericId, ct);
    }

    public async Task<IReadOnlyList<EntityItemDto>> HistoryAsync(string kind, string id, int limit, CancellationToken ct)
    {
        var current = await DetailAsync(kind, id, ct);
        if (current is null || current.RawMessageId is null) return [];
        var definitions = await DefinitionsAsync(ct);
        var result = new List<EntityItemDto>();
        foreach (var definition in definitions)
            result.AddRange(await ReadByRawMessageAsync(definition, current.RawMessageId, limit, ct));
        return result.OrderByDescending(x => x.OccurredAt).Take(limit).ToArray();
    }

    private async Task<IReadOnlyList<EntityItemDto>> ReadRowsAsync(EntityDefinitionDto definition, DateTimeOffset? at, int limit, CancellationToken ct, string? search = null, bool applyLifetime = false, int[]? sourceIds = null, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        ValidatePhysicalName(definition.TableName);
        var idColumn = IdColumn(definition);
        var timeColumn = OptionalConfiguredColumn(definition, EffectiveTimeField(definition));
        var geometryColumn = OptionalConfiguredColumn(definition, definition.Map.GeometryField);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var clauses = new List<string>();
        if (at is not null && timeColumn is not null) clauses.Add($"{Quote(timeColumn)} <= @at");
        if (applyLifetime && definition.Map.LifetimeMinutes is > 0 && timeColumn is not null) clauses.Add($"{Quote(timeColumn)} >= @cutoff");
        if (!string.IsNullOrWhiteSpace(search)) clauses.Add("to_jsonb(t)::text ILIKE @search");
        if (sourceIds is { Length: > 0 }) clauses.Add("t.raw_message_id IN (SELECT r.raw_message_id FROM raw_messages r WHERE r.source_id = ANY(@sourceIds))");
        if (from is not null && timeColumn is not null) clauses.Add($"{Quote(timeColumn)} >= @from");
        if (to is not null && timeColumn is not null) clauses.Add($"{Quote(timeColumn)} <= @to");
        var where = clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
        var order = timeColumn is not null ? $"{Quote(timeColumn)} DESC NULLS LAST, {Quote(idColumn)} DESC" : $"{Quote(idColumn)} DESC";
        var json = JsonProjection(geometryColumn);
        command.CommandText = $"SELECT ({json})::text FROM (SELECT * FROM {Quote(definition.TableName)} t {where} ORDER BY {order} LIMIT @limit) t";
        command.Parameters.AddWithValue("limit", limit);
        if (at is not null && timeColumn is not null) command.Parameters.AddWithValue("at", at.Value);
        if (applyLifetime && definition.Map.LifetimeMinutes is > 0 && timeColumn is not null) command.Parameters.AddWithValue("cutoff", (at ?? DateTimeOffset.UtcNow).AddMinutes(-definition.Map.LifetimeMinutes.Value));
        if (!string.IsNullOrWhiteSpace(search)) command.Parameters.AddWithValue("search", $"%{EscapeLike(search.Trim())}%");
        if (sourceIds is { Length: > 0 }) command.Parameters.AddWithValue("sourceIds", sourceIds);
        if (from is not null && timeColumn is not null) command.Parameters.AddWithValue("from", from.Value);
        if (to is not null && timeColumn is not null) command.Parameters.AddWithValue("to", to.Value);
        var items = new List<EntityItemDto>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) items.Add(ToItem(definition, reader.GetString(0)));
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            // A definition and its table are created in one admin transaction. During a rolling upgrade an older
            // definition can briefly point at a table not present on this replica; omit it from this snapshot.
        }
        return items;
    }

    private async Task<int> CountRowsAsync(EntityDefinitionDto definition, string? search, int[] sourceIds, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        ValidatePhysicalName(definition.TableName);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var timeColumn = OptionalConfiguredColumn(definition, EffectiveTimeField(definition));
        var clauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(search)) clauses.Add("to_jsonb(t)::text ILIKE @search");
        if (sourceIds.Length > 0) clauses.Add("t.raw_message_id IN (SELECT r.raw_message_id FROM raw_messages r WHERE r.source_id = ANY(@sourceIds))");
        if (from is not null && timeColumn is not null) clauses.Add($"{Quote(timeColumn)} >= @from");
        if (to is not null && timeColumn is not null) clauses.Add($"{Quote(timeColumn)} <= @to");
        command.CommandText = $"SELECT count(*)::int FROM {Quote(definition.TableName)} t" + (clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses));
        if (!string.IsNullOrWhiteSpace(search)) command.Parameters.AddWithValue("search", $"%{EscapeLike(search.Trim())}%");
        if (sourceIds.Length > 0) command.Parameters.AddWithValue("sourceIds", sourceIds);
        if (from is not null && timeColumn is not null) command.Parameters.AddWithValue("from", from.Value);
        if (to is not null && timeColumn is not null) command.Parameters.AddWithValue("to", to.Value);
        try { return (int)(await command.ExecuteScalarAsync(ct) ?? 0); }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703") { return 0; }
    }

    private async Task<IReadOnlyList<EntityItemDto>> ReadByRawMessageAsync(EntityDefinitionDto definition, string rawMessageId, int limit, CancellationToken ct)
    {
        ValidatePhysicalName(definition.TableName);
        if (!long.TryParse(rawMessageId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var rawId)) return [];
        var idColumn = IdColumn(definition);
        var timeColumn = OptionalConfiguredColumn(definition, EffectiveTimeField(definition));
        var geometryColumn = OptionalConfiguredColumn(definition, definition.Map.GeometryField);
        var order = timeColumn is not null ? $"{Quote(timeColumn)} DESC NULLS LAST, {Quote(idColumn)} DESC" : $"{Quote(idColumn)} DESC";
        var json = JsonProjection(geometryColumn);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT ({json})::text FROM {Quote(definition.TableName)} t WHERE raw_message_id=@rawId ORDER BY {order} LIMIT @limit";
        command.Parameters.AddWithValue("rawId", rawId);
        command.Parameters.AddWithValue("limit", limit);
        var rows = new List<EntityItemDto>();
        try { await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) rows.Add(ToItem(definition, reader.GetString(0))); }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703") { }
        return rows;
    }

    private async Task<EntityItemDto?> ReadOneAsync(EntityDefinitionDto definition, long id, CancellationToken ct)
    {
        ValidatePhysicalName(definition.TableName);
        var idColumn = IdColumn(definition);
        var geometryColumn = OptionalConfiguredColumn(definition, definition.Map.GeometryField);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var jsonProjection = JsonProjection(geometryColumn);
        command.CommandText = $"SELECT ({jsonProjection})::text FROM (SELECT * FROM {Quote(definition.TableName)} WHERE {Quote(idColumn)} = @id LIMIT 1) t";
        command.Parameters.AddWithValue("id", id);
        var json = (string?)await command.ExecuteScalarAsync(ct);
        return json is null ? null : ToItem(definition, json);
    }

    private static EntityDefinitionDto ReadDefinition(NpgsqlDataReader reader)
    {
        var fields = JsonSerializer.Deserialize<JsonElement>(reader.GetString(2));
        var mapJson = JsonSerializer.Deserialize<JsonElement>(reader.GetString(3));
        return new EntityDefinitionDto(reader.GetString(0), reader.GetString(1), fields, MapSettings.From(mapJson), reader.GetBoolean(4));
    }

    private static EntityItemDto ToItem(EntityDefinitionDto definition, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var idColumn = IdColumn(definition);
        var id = Find(root, idColumn)?.ToString() ?? string.Empty;
        var rawId = LongValue(Find(root, "raw_message_id"))?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sourceId = LongValue(Find(root, "__sourceId")) is { } source ? checked((int)source) : (int?)null;
        var occurred = DateValue(Find(root, ToSnakeCase(EffectiveTimeField(definition)))) ?? DateValue(Find(root, "created_at"));
        var geometry = GeometryValue(root, definition.Map);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in definition.Fields.EnumerateArray())
        {
            if (!field.TryGetProperty("name", out var nameNode) || nameNode.GetString() is not { } name) continue;
            if (Find(root, ToSnakeCase(name)) is { } value) values[name] = value.Clone();
        }
        return new EntityItemDto(definition.EntityName, definition.TableName, id, rawId, sourceId, occurred, JsonSerializer.SerializeToElement(values), geometry);
    }

    private static JsonElement? GeometryValue(JsonElement row, MapSettings map)
    {
        var geometry = Find(row, ToSnakeCase(map.GeometryField));
        if (geometry is { ValueKind: JsonValueKind.Object }) return geometry.Value.Clone();
        var lat = DoubleValue(Find(row, ToSnakeCase(map.LatitudeField)));
        var lon = DoubleValue(Find(row, ToSnakeCase(map.LongitudeField)));
        if (lat is null || lon is null) return null;
        return JsonSerializer.SerializeToElement(new { type = "Point", coordinates = new[] { lon.Value, lat.Value } });
    }

    private static JsonElement? Find(JsonElement row, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var property in row.EnumerateObject()) if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static long? LongValue(JsonElement? value)
    {
        if (value is not { } v) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var number)) return number;
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out number) ? number : null;
    }
    private static double? DoubleValue(JsonElement? value)
    {
        if (value is not { } v) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var number)) return number;
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out number) ? number : null;
    }
    private static DateTimeOffset? DateValue(JsonElement? value) => value is { ValueKind: JsonValueKind.String } v && DateTimeOffset.TryParse(v.GetString(), out var result) ? result : null;
    private static string IdColumn(EntityDefinitionDto definition) => definition.EntityName + "Id";
    private static string JsonProjection(string? geometryColumn)
    {
        var projection = "to_jsonb(t) || jsonb_build_object('__sourceId', (SELECT r.source_id FROM raw_messages r WHERE r.raw_message_id = t.raw_message_id))";
        return geometryColumn is null
            ? projection
            : $"{projection} || jsonb_build_object('{geometryColumn}', ST_AsGeoJSON(t.{Quote(geometryColumn)})::jsonb)";
    }
    private static bool SameKind(EntityDefinitionDto definition, string kind) => definition.EntityName.Equals(kind, StringComparison.OrdinalIgnoreCase) || definition.TableName.Equals(kind, StringComparison.OrdinalIgnoreCase);
    private static string? OptionalConfiguredColumn(EntityDefinitionDto definition, string? column) => string.IsNullOrWhiteSpace(column) ? null : ValidateConfiguredColumn(definition, column);
    private static string ValidateConfiguredColumn(EntityDefinitionDto definition, string column)
    {
        ValidateIdentifier(column);
        if (!FieldNames(definition).Contains(column, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"Unknown configured column '{column}'.");
        return ToSnakeCase(column);
    }
    private static IEnumerable<string> FieldNames(EntityDefinitionDto definition) => definition.Fields.ValueKind == JsonValueKind.Array
        ? definition.Fields.EnumerateArray().Select(x => x.TryGetProperty("name", out var name) ? name.GetString() : null).OfType<string>()
        : [];
    private static string? EffectiveTimeField(EntityDefinitionDto definition) => definition.Map.TimeField ?? FieldNames(definition).FirstOrDefault(x => x.Equals("occurredAt", StringComparison.OrdinalIgnoreCase));
    private static string Quote(string identifier) { ValidateIdentifier(identifier); return $"\"{identifier}\""; }
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    private static int[] ParseIds(string? value) => string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => int.TryParse(x, out var id) && id > 0 ? id : 0).Where(x => x > 0).Distinct().ToArray();
    private static void ValidatePhysicalName(string identifier) { ValidateIdentifier(identifier); if (!identifier.StartsWith("ee_", StringComparison.Ordinal)) throw new InvalidOperationException("Entity table must use the ee_ prefix."); }
    private static void ValidateIdentifier(string identifier) { if (!Identifier().IsMatch(identifier)) throw new InvalidOperationException($"Invalid database identifier '{identifier}'."); }
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
    private static string ToSnakeCase(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : Regex.Replace(value, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();
}

public sealed record EntityDefinitionDto(string EntityName, string TableName, JsonElement Fields, MapSettings Map, bool Enabled);
public sealed record MapSettings(bool Visible, string Renderer, string? LabelField, string? TimeField, string? StatusField, string? GeometryField, string? LatitudeField, string? LongitudeField, int? LifetimeMinutes, string? SvgIcon, string? Color, double? Width, double? Opacity, string? Dash)
{
    public static MapSettings From(JsonElement value) => new(
        Bool(value, "enabled") ?? false, String(value, "renderer") ?? "point", String(value, "labelField"), String(value, "timeField"), String(value, "statusField"),
        String(value, "geometryField"), String(value, "latitudeField"), String(value, "longitudeField"), Int(value, "lifetimeMinutes"), String(value, "svg") ?? String(value, "svgIcon"),
        String(value, "color"), Double(value, "width"), Double(value, "opacity"), String(value, "dash"));
    private static JsonElement? Property(JsonElement value, string name) { if (value.ValueKind != JsonValueKind.Object) return null; foreach (var p in value.EnumerateObject()) if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value; return null; }
    private static string? String(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } p ? p.GetString() : null;
    private static bool? Bool(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.True } ? true : Property(value, name) is { ValueKind: JsonValueKind.False } ? false : null;
    private static int? Int(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.Number } p && p.TryGetInt32(out var result) ? result : null;
    private static double? Double(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.Number } p && p.TryGetDouble(out var result) ? result : null;
}
public sealed record EntityItemDto(string Entity, string Table, string Id, string? RawMessageId, int? SourceId, DateTimeOffset? OccurredAt, JsonElement Values, JsonElement? Geometry);
public sealed record EntitySnapshotDto(DateTimeOffset GeneratedAt, DateTimeOffset? At, IReadOnlyList<EntityItemDto> Items, bool Truncated, int LimitPerEntity);
public sealed record EntityPageDto(IReadOnlyList<EntityItemDto> Items, string? NextCursor, int TotalCount);
