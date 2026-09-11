using Microsoft.Extensions.Time.Testing;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;

namespace RateLimitHeaders.Tests.AuditFixes;

/// <summary>
/// Scenarios STATE10, STATE11, STATE41, STATE14, STATE15 from PLAN-audit-fixes.md (task T9):
/// the tracker's read path subtracts elapsed time from the stored reset value and reports
/// an elapsed window as invalid; <c>TryGetThrottlingContext</c> carries sub-second timing.
/// </summary>
public class AdjustedReadTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static (FakeTimeProvider Clock, RateLimitStateTracker Tracker) CreateTrackerAtStart()
    {
        var clock = new FakeTimeProvider(Start);
        var tracker = new RateLimitStateTracker(clock);
        return (clock, tracker);
    }

    private static RateLimitInfo FiveOfHundredResetSixty() => new()
    {
        PolicyName = "default",
        Remaining = 5,
        Quota = 100,
        ResetSeconds = 60,
        IsValid = true
    };

    // STATE10: reads subtract elapsed time. Red baseline today: ResetSeconds is still 60.
    [Fact]
    public void Read_SubtractsElapsedTime()
    {
        var (clock, tracker) = CreateTrackerAtStart();
        tracker.UpdateState("key", FiveOfHundredResetSixty());

        clock.Advance(TimeSpan.FromSeconds(45));
        var info = tracker.GetRateLimitInfo("key");

        info.IsValid.Should().BeTrue();
        info.Remaining.Should().Be(5);
        info.ResetSeconds.Should().Be(15);
    }

    // STATE11: an elapsed window stops driving decisions.
    // Red baseline today: the raw snapshot is returned for up to an hour.
    [Fact]
    public void Read_ReportsInvalidAfterTheWindowElapsed()
    {
        var (clock, tracker) = CreateTrackerAtStart();
        tracker.UpdateState("key", FiveOfHundredResetSixty());

        clock.Advance(TimeSpan.FromSeconds(61));

        tracker.GetRateLimitInfo("key").IsValid.Should().BeFalse();
    }

    // STATE41: at exactly the reset moment the state is already invalid (the window is over
    // at its own reset instant); one second earlier the read is still valid with 1 second left.
    [Fact]
    public void Read_IsInvalidAtExactlyTheResetMoment()
    {
        var (clockAtReset, trackerAtReset) = CreateTrackerAtStart();
        trackerAtReset.UpdateState("key", FiveOfHundredResetSixty());
        clockAtReset.Advance(TimeSpan.FromSeconds(60));
        trackerAtReset.GetRateLimitInfo("key").IsValid.Should().BeFalse();

        var (clockBefore, trackerBefore) = CreateTrackerAtStart();
        trackerBefore.UpdateState("key", FiveOfHundredResetSixty());
        clockBefore.Advance(TimeSpan.FromSeconds(59));
        var info = trackerBefore.GetRateLimitInfo("key");
        info.IsValid.Should().BeTrue();
        info.ResetSeconds.Should().Be(1);
    }

    // STATE14: the throttling context carries sub-second precision.
    [Fact]
    public void ThrottlingContext_CarriesSubSecondTiming()
    {
        var (clock, tracker) = CreateTrackerAtStart();
        tracker.UpdateState("key", FiveOfHundredResetSixty());

        clock.Advance(TimeSpan.FromSeconds(59.5));

        tracker.TryGetThrottlingContext("key", out var context).Should().BeTrue();
        context.TimeUntilReset.Should().Be(TimeSpan.FromMilliseconds(500));
        context.TimeSinceObserved.Should().Be(TimeSpan.FromSeconds(59.5));
        context.ObservedAt.Should().Be(Start);
    }

    // STATE15: aggregate reads use the same adjusted view.
    [Fact]
    public void GetAllStates_ReturnsAdjustedViews()
    {
        var (clock, tracker) = CreateTrackerAtStart();
        tracker.UpdateState("a", FiveOfHundredResetSixty());
        tracker.UpdateState("b", FiveOfHundredResetSixty() with { ResetSeconds = 120 });

        clock.Advance(TimeSpan.FromSeconds(30));
        var resets = tracker.GetAllStates().Select(s => s.ResetSeconds).OrderBy(r => r).ToList();

        resets.Should().Equal(30, 90);
    }
}
