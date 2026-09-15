using Puluj.Admin;

namespace Puluj.Admin.Tests;

public sealed class DatabaseQueryGuardTests
{
    [Theory]
    [InlineData("SELECT * FROM raw_messages")]
    [InlineData("  with recent as (select 1) select * from recent")]
    public void Allows_one_read_query(string sql)
    {
        Assert.True(DatabaseQueryGuard.TryValidate(sql, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("DELETE FROM raw_messages")]
    [InlineData("SELECT 1; DELETE FROM raw_messages")]
    [InlineData("SELECT * FROM sources -- token")]
    [InlineData("SELECT secrets FROM sources")]
    [InlineData("SELECT * FROM app_settings")]
    [InlineData("SELECT pg_read_file('postgresql.conf')")]
    public void Rejects_writes_comments_and_sensitive_access(string sql)
    {
        Assert.False(DatabaseQueryGuard.TryValidate(sql, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
