using System.Text.RegularExpressions;

namespace Puluj.Admin;

/// <summary>
/// A deliberately small front-door for the SQL console. PostgreSQL still runs the command in a read-only transaction;
/// this guard rejects multi-statements, comments and known secret/system access before a connection is occupied.
/// </summary>
public static partial class DatabaseQueryGuard
{
    private const int MaxLength = 12_000;

    public static bool TryValidate(string? sql, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(sql))
        {
            error = "Введіть SQL-запит.";
            return false;
        }
        if (sql.Length > MaxLength)
        {
            error = $"Запит задовгий: максимум {MaxLength:N0} символів.";
            return false;
        }
        if (sql.Contains(';') || sql.Contains("--", StringComparison.Ordinal) || sql.Contains("/*", StringComparison.Ordinal))
        {
            error = "Дозволено один запит без коментарів і крапки з комою.";
            return false;
        }
        if (!StartsWithSelect().IsMatch(sql))
        {
            error = "Дозволені лише SELECT або WITH … SELECT запити.";
            return false;
        }
        // The table browser redacts sensitive columns. The console refuses them entirely, so aliases cannot bypass redaction.
        if (SensitiveAccess().IsMatch(sql))
        {
            error = "Запит не може читати секрети, налаштування або системні pg_* функції.";
            return false;
        }
        return true;
    }

    [GeneratedRegex(@"^\s*(select\b|with\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StartsWithSelect();

    [GeneratedRegex(@"\b(app_settings|secrets|token|password|api[_-]?key|pg_[a-z0-9_]*|dblink|lo_[a-z0-9_]*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAccess();
}
