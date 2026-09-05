using System.Reflection;
using RateLimitHeaders.Http;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Throttling;

namespace RateLimitHeaders.Tests.AuditFixes;

/// <summary>
/// Scenarios STATE01-STATE09 from PLAN-audit-fixes.md (tasks T3 and T8):
/// non-finite, null, and out-of-range configuration values are rejected early,
/// and the percentage algorithm clamps its computed delay before the TimeSpan conversion.
/// </summary>
public class ValidationHardeningTests
{
    // STATE01: NaN cannot silently disable the low-quota callback threshold.
    // Red baseline today: both range comparisons are false for NaN, so the assignment succeeds.
    [Fact]
    public void QuotaLowThreshold_RejectsNaN()
    {
        var options = new RateLimitAwareOptions();

        var act = () => options.QuotaLowThreshold = double.NaN;

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // STATE02: the shared validation helper rejects every non-finite threshold.
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ValidateQuotaThreshold_RejectsNonFiniteValues(double value)
    {
        var act = () => RateLimitOptionsHelper.ValidateQuotaThreshold(value, "value");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // STATE03: a null algorithm is refused at assignment.
    [Fact]
    public void ThrottlingAlgorithm_RejectsNullAtAssignment()
    {
        var options = new RateLimitAwareOptions();

        var act = () => options.ThrottlingAlgorithm = null!;

        act.Should().Throw<ArgumentNullException>();
    }

    // STATE04: a handler built over a null algorithm fails at construction, not first send.
    // The setter now rejects null, so the only route to a null algorithm is reflection;
    // the constructor check is the belt behind that suspender.
    [Fact]
    public void HandlerConstruction_RejectsNullAlgorithm()
    {
        var options = new RateLimitAwareOptions();
        var backingField = typeof(RateLimitAwareOptions)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => typeof(IThrottlingAlgorithm).IsAssignableFrom(f.FieldType));
        backingField.SetValue(options, null);

        var act = () => new RateLimitAwareHandler(options);

        act.Should().Throw<ArgumentException>().WithMessage("*ThrottlingAlgorithm*");
    }

    // STATE05: delay caps beyond what Task.Delay accepts are rejected.
    [Fact]
    public void ValidateDelay_RejectsOutOfRangeValues()
    {
        var tooLong = () => RateLimitOptionsHelper.ValidateDelay(TimeSpan.FromDays(60), "value");
        var negative = () => RateLimitOptionsHelper.ValidateDelay(TimeSpan.FromSeconds(-1), "value");

        tooLong.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    // STATE06: NaN threshold fails at construction, not per evaluation.
    [Fact]
    public void AlgorithmConstruction_RejectsNaNThreshold()
    {
        var act = () => new PercentageThrottlingAlgorithm(double.NaN, 1.0, TimeSpan.FromSeconds(5));

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("threshold");
    }

    // STATE07: an infinite factor fails at construction.
    [Fact]
    public void AlgorithmConstruction_RejectsInfiniteFactor()
    {
        var act = () => new PercentageThrottlingAlgorithm(0.1, double.PositiveInfinity, TimeSpan.FromSeconds(5));

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("factor");
    }

    // STATE08: a delay cap beyond Task.Delay's ceiling (~49.7 days) fails at construction.
    [Fact]
    public void AlgorithmConstruction_RejectsDelayCapBeyondTaskDelayCeiling()
    {
        var act = () => new PercentageThrottlingAlgorithm(0.1, 1.0, TimeSpan.FromDays(60));

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxDelay");
    }

    // STATE09: the audit's overflow input returns the cap instead of throwing.
    // Red baseline today: TimeSpan.FromSeconds overflows before the cap is applied.
    [Fact]
    public void Evaluate_ClampsBeforeTimeSpanConversion()
    {
        var algorithm = new PercentageThrottlingAlgorithm(0.1, 5000, TimeSpan.FromSeconds(5));
        var info = new RateLimitInfo
        {
            PolicyName = "default",
            Remaining = 1,
            Quota = int.MaxValue,
            ResetSeconds = int.MaxValue,
            IsValid = true
        };

        var result = algorithm.Evaluate(info);

        result.ShouldThrottle.Should().BeTrue();
        result.Delay.Should().Be(TimeSpan.FromSeconds(5));
    }
}
