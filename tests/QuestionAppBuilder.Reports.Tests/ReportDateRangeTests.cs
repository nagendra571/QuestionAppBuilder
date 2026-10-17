using FluentAssertions;
using QuestionAppBuilder.Reports.Time;

namespace QuestionAppBuilder.Reports.Tests;

public sealed class ReportDateRangeTests
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    [Fact]
    public void DST_date_filter_uses_half_open_UTC_range()
    {
        var range = ReportDateRange.FromLocalDates(new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 8), Eastern);

        range.FromUtc.Should().Be(new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc));
        range.ToUtcExclusive.Should().Be(new DateTime(2026, 3, 9, 4, 0, 0, DateTimeKind.Utc));
        (range.ToUtcExclusive!.Value - range.FromUtc!.Value).Should().Be(TimeSpan.FromHours(23));
    }

    [Fact]
    public void Ambiguous_local_boundary_uses_the_earlier_UTC_occurrence()
    {
        var utc = ReportDateRange.ResolveBoundary(new DateTime(2026, 11, 1, 1, 0, 0), Eastern);

        utc.Should().Be(new DateTime(2026, 11, 1, 5, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Invalid_local_boundary_is_rejected()
    {
        var act = () => ReportDateRange.ResolveBoundary(new DateTime(2026, 3, 8, 2, 30, 0), Eastern);

        act.Should().Throw<ArgumentException>();
    }
}
