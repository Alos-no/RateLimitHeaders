using RateLimitHeaders.Parsing;

namespace RateLimitHeaders.Tests.AuditFixes;

/// <summary>
/// Scenarios PARSE10-PARSE15 from PLAN-audit-fixes.md (task T2):
/// <see cref="RateLimitInfo"/> distinguishes values the server sent from values it never sent,
/// carries 64-bit values, clamps the reset conversion, and marks Retry-After state.
/// </summary>
public class RateLimitInfoAbsenceTests
{
    // PARSE10: sent and never-sent numbers are distinguishable.
    [Fact]
    public void SetAndUnsetValues_AreDistinguishable()
    {
        var withRemaining = new RateLimitInfo { Remaining = 5, IsValid = true };
        var empty = default(RateLimitInfo);

        withRemaining.HasRemaining.Should().BeTrue();
        withRemaining.Remaining.Should().Be(5);
        withRemaining.HasQuota.Should().BeFalse();
        withRemaining.HasResetSeconds.Should().BeFalse();
        withRemaining.Quota.Should().Be(0);

        empty.HasRemaining.Should().BeFalse();
        empty.HasResetSeconds.Should().BeFalse();
        empty.HasQuota.Should().BeFalse();
        empty.HasWindowSeconds.Should().BeFalse();
    }

    // PARSE11: values beyond int.MaxValue survive without truncation.
    [Fact]
    public void ValuesBeyondInt32_AreCarriedExactly()
    {
        var info = new RateLimitInfo
        {
            Quota = 5_000_000_000L,
            Remaining = 4_000_000_000L,
            IsValid = true
        };

        info.Quota.Should().Be(5_000_000_000L);
        info.Remaining.Should().Be(4_000_000_000L);
    }

    // PARSE12: the fraction reports unknown (null) instead of a misleading 1.0;
    // GetRemainingPercentage keeps its old behavior for source compatibility.
    [Fact]
    public void RemainingFraction_ReportsUnknownWhenQuotaUnknown()
    {
        var known = new RateLimitInfo { Remaining = 5, Quota = 100, IsValid = true };
        var unknownQuota = new RateLimitInfo { Remaining = 5, IsValid = true };

        known.RemainingFraction.Should().Be(0.05);
        unknownQuota.RemainingFraction.Should().BeNull();

        known.GetRemainingPercentage().Should().Be(0.05);
        unknownQuota.GetRemainingPercentage().Should().Be(1.0);
    }

    // PARSE13: exhaustion (a sent zero) is distinguishable from an absent count.
    [Fact]
    public void IsExhausted_RequiresASentZero()
    {
        var exhausted = new RateLimitInfo { Remaining = 0, IsValid = true };
        var noCount = new RateLimitInfo { IsValid = true };

        exhausted.IsExhausted.Should().BeTrue();
        noCount.IsExhausted.Should().BeFalse();
        noCount.Remaining.Should().Be(0, "the getter still reads 0 even though nothing was sent");
    }

    // PARSE14: a 15-digit reset value cannot make the TimeSpan conversion throw.
    [Fact]
    public void ResetAfter_ClampsAt365Days_AndIsZeroWhenUnset()
    {
        var huge = new RateLimitInfo { ResetSeconds = 999_999_999_999_999L, IsValid = true };
        var unset = new RateLimitInfo { IsValid = true };

        huge.ResetAfter.Should().Be(TimeSpan.FromDays(365));
        unset.ResetAfter.Should().Be(TimeSpan.Zero);
    }

    // PARSE15: Retry-After state is marked as such on both construction paths.
    [Fact]
    public void RetryAfterState_IsMarked()
    {
        var created = RateLimitInfo.CreateFromRetryAfter(120);

        var overridden = new RateLimitInfo
        {
            PolicyName = "default",
            Remaining = 40,
            Quota = 100,
            ResetSeconds = 30,
            IsValid = true
        }.WithRetryAfterOverride(120);

        foreach (var info in new[] { created, overridden })
        {
            info.HasRetryAfter.Should().BeTrue();
            info.HasRemaining.Should().BeTrue();
            info.Remaining.Should().Be(0);
            info.IsExhausted.Should().BeTrue();
            info.ResetSeconds.Should().Be(120);
        }
    }
}
