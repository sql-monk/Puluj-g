using Npgsql;
using Puluj.Contracts;

namespace Puluj.Admin;

/// <summary>
/// Turns a failure of an operator's read-only query into something the SQL console can show: PostgreSQL's own message,
/// its SQLSTATE and the character position it points at. Anything that is not a query error (a lost connection, a bug)
/// returns null and stays an internal server error, so no stack trace or connection detail reaches the page.
/// </summary>
public static class DatabaseQueryErrors
{
    public static DbQueryErrorDto? Describe(Exception error) => error switch
    {
        PostgresException { SqlState: PostgresErrorCodes.QueryCanceled } => Timeout,
        PostgresException pg => new DbQueryErrorDto(
            $"Помилка SQL: {pg.MessageText}", pg.SqlState, pg.Position > 0 ? pg.Position : null, string.IsNullOrWhiteSpace(pg.Hint) ? null : pg.Hint),
        NpgsqlException { InnerException: TimeoutException } => Timeout,
        _ => null,
    };

    private static readonly DbQueryErrorDto Timeout = new("Запит перевищив ліміт 10 с і був скасований.", PostgresErrorCodes.QueryCanceled, null, null);
}
