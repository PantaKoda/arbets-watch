using ArbetsWatch.Core.Time;

namespace ArbetsWatch.Core.Tests;

public sealed class SwedishTimeTests
{
    [Fact]
    public void Summer_local_time_is_two_hours_ahead_of_utc()
    {
        // Observed live: publication_date 19:02:25 with timestamp 17:02:25.951Z.
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 17, 2, 25, TimeSpan.Zero), SwedishTime.ParseLocal("2026-10-06T19:02:25"));
    }

    [Fact]
    public void Winter_local_time_is_one_hour_ahead_of_utc()
    {
        Assert.Equal(new DateTimeOffset(2027, 1, 15, 11, 0, 0, TimeSpan.Zero), SwedishTime.ParseLocal("2027-01-15T12:00:00"));
    }

    [Fact]
    public void Repeated_autumn_hour_reads_as_the_earlier_instant()
    {
        // 2026-10-25 03:00 summer time → 02:00 winter time; 02:30 occurs twice.
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), SwedishTime.ParseLocal("2026-10-25T02:30:00"));
    }

    [Fact]
    public void Repeated_autumn_hour_can_read_as_the_later_instant()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero), SwedishTime.ParseLocal("2026-10-25T02:30:00", laterInRepeatedHour: true));
    }

    [Fact]
    public void Spring_gap_time_reads_with_the_winter_offset()
    {
        // 2027-03-28 02:00 → 03:00; 02:30 does not exist locally.
        Assert.Equal(new DateTimeOffset(2027, 3, 28, 1, 30, 0, TimeSpan.Zero), SwedishTime.ParseLocal("2027-03-28T02:30:00"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    [InlineData("2026-10-06")]
    public void Missing_or_invalid_text_is_null(string? text) => Assert.Null(SwedishTime.ParseLocal(text));

    [Fact]
    public void Query_bounds_carry_the_offset_of_their_instant()
    {
        Assert.Equal("2026-10-06T19:00:00+02:00", SwedishTime.FormatQueryBound(new DateTimeOffset(2026, 10, 6, 17, 0, 0, 999, TimeSpan.Zero)));
        Assert.Equal("2027-01-15T12:00:00+01:00", SwedishTime.FormatQueryBound(new DateTimeOffset(2027, 1, 15, 11, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Query_bounds_distinguish_both_occurrences_of_the_repeated_hour()
    {
        var first = new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero);
        var second = first.AddHours(1);
        Assert.Equal("2026-10-25T02:30:00+02:00", SwedishTime.FormatQueryBound(first));
        Assert.Equal("2026-10-25T02:30:00+01:00", SwedishTime.FormatQueryBound(second));
    }

    [Fact]
    public void Consecutive_bounds_across_the_spring_transition_are_contiguous()
    {
        var before = new DateTimeOffset(2027, 3, 28, 0, 59, 59, TimeSpan.Zero);
        Assert.Equal("2027-03-28T01:59:59+01:00", SwedishTime.FormatQueryBound(before));
        Assert.Equal("2027-03-28T03:00:00+02:00", SwedishTime.FormatQueryBound(before.AddSeconds(1)));
    }
}
