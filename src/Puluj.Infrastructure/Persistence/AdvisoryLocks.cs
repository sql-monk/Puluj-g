namespace Puluj.Infrastructure.Persistence;

/// <summary>
/// Transaction-scoped PostgreSQL advisory locks for derived state. Store protects the entire read-modify-write
/// transaction, including target triggers and their cross-category source statistics. Order: raw rows, Store,
/// derived rows. Parsing and messages without derived effects do not acquire Store.
/// </summary>
public static class AdvisoryLocks
{
    /// <summary>Conservative store boundary; the granular experiment is excluded pending P09 SQL/concurrency work.</summary>
    public const long Store = 0x50554C554A01;

    public static FormattableString Take(long key) => $"SELECT pg_advisory_xact_lock({key})";

    /// <summary>
    /// Session-level variant: survives the commit of the transaction that took it and is released by
    /// <see cref="ReleaseSession"/> (or when the connection closes). Conflicts with <see cref="Take"/> on the same key,
    /// so a holder can span two transactions where the second must not be raced by a Store writer (ReprocessService).
    /// </summary>
    public static FormattableString TakeSession(long key) => $"SELECT pg_advisory_lock({key})";

    public static FormattableString ReleaseSession(long key) => $"SELECT pg_advisory_unlock({key})";
}
