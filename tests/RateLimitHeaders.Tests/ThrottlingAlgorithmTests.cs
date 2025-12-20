using RateLimitHeaders.Parsing;
using RateLimitHeaders.Throttling;

namespace RateLimitHeaders.Tests;

public class ThrottlingAlgorithmTests
{
    public class PercentageThrottlingAlgorithmTests
    {
        [Fact]
        public void Constructor_WithDefaultValues_ShouldUseDefaults()
        {
            var algorithm = new PercentageThrottlingAlgorithm();

            algorithm.Threshold.Should().Be(PercentageThrottlingAlgorithm.DefaultThreshold);
            algorithm.Factor.Should().Be(PercentageThrottlingAlgorithm.DefaultFactor);
            algorithm.MaxDelay.Should().Be(PercentageThrottlingAlgorithm.DefaultMaxDelay);
        }

        [Fact]
        public void Constructor_WithCustomValues_ShouldUseProvided()
        {
            var algorithm = new PercentageThrottlingAlgorithm(
                threshold: 0.2,
                factor: 1.5,
                maxDelay: TimeSpan.FromSeconds(10));

            algorithm.Threshold.Should().Be(0.2);
            algorithm.Factor.Should().Be(1.5);
            algorithm.MaxDelay.Should().Be(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(-0.1)]
        [InlineData(1.1)]
        public void Constructor_WithInvalidThreshold_ShouldThrow(double threshold)
        {
            var act = () => new PercentageThrottlingAlgorithm(threshold, 1.0, TimeSpan.FromSeconds(5));

            act.Should().Throw<ArgumentOutOfRangeException>()
                .WithMessage("*threshold*");
        }

        [Fact]
        public void Constructor_WithNegativeFactor_ShouldThrow()
        {
            var act = () => new PercentageThrottlingAlgorithm(0.1, -1.0, TimeSpan.FromSeconds(5));

            act.Should().Throw<ArgumentOutOfRangeException>()
                .WithMessage("*factor*");
        }

        [Fact]
        public void Constructor_WithNegativeMaxDelay_ShouldThrow()
        {
            var act = () => new PercentageThrottlingAlgorithm(0.1, 1.0, TimeSpan.FromSeconds(-1));

            act.Should().Throw<ArgumentOutOfRangeException>()
                .WithMessage("*delay*");
        }

        [Fact]
        public void Evaluate_WhenAboveThreshold_ShouldNotThrottle()
        {
            var algorithm = new PercentageThrottlingAlgorithm();
            var info = new RateLimitInfo
            {
                Remaining = 20,  // 20% remaining
                Quota = 100,
                ResetSeconds = 60,
                IsValid = true
            };

            var result = algorithm.Evaluate(info);

            result.ShouldThrottle.Should().BeFalse();
            result.Delay.Should().Be(TimeSpan.Zero);
        }

        [Fact]
        public void Evaluate_WhenBelowThreshold_ShouldThrottle()
        {
            var algorithm = new PercentageThrottlingAlgorithm();
            var info = new RateLimitInfo
            {
                Remaining = 5,  // 5% remaining (below 10% threshold)
                Quota = 100,
                ResetSeconds = 60,
                IsValid = true
            };

            var result = algorithm.Evaluate(info);

            result.ShouldThrottle.Should().BeTrue();
            result.Delay.Should().BeGreaterThan(TimeSpan.Zero);
            result.Reason.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public void Evaluate_ShouldCalculateCorrectDelay()
        {
            // With default settings: threshold=0.1, factor=1.0, maxDelay=5s
            // At 5% remaining with 60s reset: delay = (0.10 - 0.05) * 60 * 1.0 = 3.0s
            var algorithm = new PercentageThrottlingAlgorithm();
            var info = new RateLimitInfo
            {
                Remaining = 5,
                Quota = 100,
                ResetSeconds = 60,
                IsValid = true
            };

            var result = algorithm.Evaluate(info);

            result.Delay.TotalSeconds.Should().BeApproximately(3.0, 0.1);
        }

        [Fact]
        public void Evaluate_ShouldRespectMaxDelay()
        {
            var algorithm = new PercentageThrottlingAlgorithm(
                threshold: 0.5,  // 50% threshold
                factor: 2.0,
                maxDelay: TimeSpan.FromSeconds(2));

            var info = new RateLimitInfo
            {
                Remaining = 1,  // 1% remaining
                Quota = 100,
                ResetSeconds = 60,
                IsValid = true
            };

            var result = algorithm.Evaluate(info);

            // Calculated delay would be (0.5 - 0.01) * 60 * 2.0 = 58.8s
            // But max delay is 2s
            result.Delay.Should().Be(TimeSpan.FromSeconds(2));
        }

        [Fact]
        public void Evaluate_WhenInfoNotValid_ShouldNotThrottle()
        {
            var algorithm = new PercentageThrottlingAlgorithm();
            var info = new RateLimitInfo
            {
                Remaining = 0,
                Quota = 100,
                IsValid = false
            };

            var result = algorithm.Evaluate(info);

            result.ShouldThrottle.Should().BeFalse();
        }

        [Fact]
        public void Evaluate_WhenQuotaIsZero_ShouldNotThrottle()
        {
            var algorithm = new PercentageThrottlingAlgorithm();
            var info = new RateLimitInfo
            {
                Remaining = 0,
                Quota = 0,
                IsValid = true
            };

            var result = algorithm.Evaluate(info);

            result.ShouldThrottle.Should().BeFalse();
        }

        [Fact]
        public void Evaluate_WithNegligibleDelay_ShouldNotThrottle()
        {
            // If calculated delay is less than 10ms, don't bother throttling
            var algorithm = new PercentageThrottlingAlgorithm(
                threshold: 0.1,
                factor: 0.001,  // Very small factor
                maxDelay: TimeSpan.FromSeconds(5));

            var info = new RateLimitInfo
            {
                Remaining = 9,  // Just below threshold
                Quota = 100,
                ResetSeconds = 1,  // Very short window
                IsValid = true
            };

            var result = algorithm.Evaluate(info);

            result.ShouldThrottle.Should().BeFalse();
        }

        [Fact]
        public void Evaluate_AtExactThreshold_ShouldNotThrottle()
        {
            var algorithm = new PercentageThrottlingAlgorithm(threshold: 0.1, factor: 1.0, maxDelay: TimeSpan.FromSeconds(5));
            var info = new RateLimitInfo
            {
                Remaining = 10,  // Exactly 10% remaining
                Quota = 100,
                ResetSeconds = 60,
                IsValid = true
            };

            var result = algorithm.Evaluate(info);

            // At exactly the threshold, (threshold - remaining) = 0, so no delay
            result.ShouldThrottle.Should().BeFalse();
        }
    }

    public class ThrottlingResultTests
    {
        [Fact]
        public void NoThrottle_ShouldHaveCorrectValues()
        {
            var result = ThrottlingResult.NoThrottle;

            result.ShouldThrottle.Should().BeFalse();
            result.Delay.Should().Be(TimeSpan.Zero);
            result.Reason.Should().BeNull();
        }

        [Fact]
        public void Throttle_WithTimeSpan_ShouldCreateResult()
        {
            var delay = TimeSpan.FromSeconds(2.5);
            var result = ThrottlingResult.Throttle(delay, "test reason");

            result.ShouldThrottle.Should().BeTrue();
            result.Delay.Should().Be(delay);
            result.Reason.Should().Be("test reason");
        }

        [Fact]
        public void Throttle_WithMilliseconds_ShouldCreateResult()
        {
            var result = ThrottlingResult.Throttle(1500, "test reason");

            result.ShouldThrottle.Should().BeTrue();
            result.Delay.Should().Be(TimeSpan.FromMilliseconds(1500));
            result.Reason.Should().Be("test reason");
        }
    }
}
