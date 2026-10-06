using ArbetsWatch.Core.Platform;
using Microsoft.Extensions.Time.Testing;

namespace ArbetsWatch.Core.Tests;

public sealed class ResumeDetectorTests
{
    [Fact]
    public void Regular_ticks_are_not_a_resume_and_a_long_gap_is()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero));
        var detector = new ResumeDetector(time, TimeSpan.FromMinutes(2));

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(detector.Tick());
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(detector.Tick());

        // The machine slept for an hour: the next tick arrives late.
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromHours(1), detector.Tick());
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(detector.Tick());
    }
}
