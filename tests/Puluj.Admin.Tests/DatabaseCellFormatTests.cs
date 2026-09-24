using Puluj.Admin;

namespace Puluj.Admin.Tests;

/// <summary>Admin re-audit R05: the table browser prints timestamps in ISO 8601 with their zone, not "09/21/2026 17:54:54".</summary>
public sealed class DatabaseCellFormatTests
{
    [Fact]
    public void Timestamptz_is_iso_utc_with_z()
    {
        Assert.Equal("2026-09-21T17:54:54Z", DatabaseCellFormat.Format(new DateTime(2026, 9, 21, 17, 54, 54, DateTimeKind.Utc)));
        Assert.Equal("2026-09-21T17:54:54.5Z", DatabaseCellFormat.Format(new DateTime(2026, 9, 21, 17, 54, 54, 500, DateTimeKind.Utc)));
    }

    [Fact]
    public void Timestamp_without_zone_stays_without_zone_and_offsets_are_kept()
    {
        Assert.Equal("2026-09-21T17:54:54", DatabaseCellFormat.Format(new DateTime(2026, 9, 21, 17, 54, 54, DateTimeKind.Unspecified)));
        Assert.Equal("2026-09-21T20:54:54+03:00", DatabaseCellFormat.Format(new DateTimeOffset(2026, 9, 21, 20, 54, 54, TimeSpan.FromHours(3))));
        Assert.Equal("2026-09-21", DatabaseCellFormat.Format(new DateOnly(2026, 9, 21)));
    }

    [Fact]
    public void Other_values_are_invariant_and_long_ones_are_cut()
    {
        Assert.Equal("1.5", DatabaseCellFormat.Format(1.5));
        var cut = DatabaseCellFormat.Format(new string('x', DatabaseCellFormat.MaxLength + 10));
        Assert.Equal(DatabaseCellFormat.MaxLength + 1, cut.Length);
        Assert.EndsWith("…", cut);
    }
}
