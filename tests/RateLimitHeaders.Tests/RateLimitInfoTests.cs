using RateLimitHeaders.Parsing;

namespace RateLimitHeaders.Tests;

public class RateLimitInfoTests
{
    [Fact]
    public void Default_ShouldHaveIsValidFalse()
    {
        var info = default(RateLimitInfo);

        info.IsValid.Should().BeFalse();
        info.PolicyName.Should().BeEmpty();  // Changed from null to empty string for safer default
        info.Remaining.Should().Be(0);
        info.ResetSeconds.Should().Be(0);
        info.Quota.Should().Be(0);
        info.WindowSeconds.Should().Be(0);
    }

    [Fact]
    public void GetRemainingPercentage_WithPositiveQuota_ShouldReturnCorrectPercentage()
    {
        var info = new RateLimitInfo
        {
            Remaining = 25,
            Quota = 100,
            IsValid = true
        };

        info.GetRemainingPercentage().Should().Be(0.25);
    }

    [Fact]
    public void GetRemainingPercentage_WithZeroQuota_ShouldReturn1()
    {
        var info = new RateLimitInfo
        {
            Remaining = 0,
            Quota = 0,
            IsValid = true
        };

        info.GetRemainingPercentage().Should().Be(1.0);
    }

    [Fact]
    public void GetRemainingPercentage_WithFullQuota_ShouldReturn1()
    {
        var info = new RateLimitInfo
        {
            Remaining = 100,
            Quota = 100,
            IsValid = true
        };

        info.GetRemainingPercentage().Should().Be(1.0);
    }

    [Theory]
    [InlineData(5, 100, 0.1, true)]    // 5% remaining, 10% threshold = low
    [InlineData(10, 100, 0.1, true)]   // 10% remaining, 10% threshold = low (at threshold)
    [InlineData(15, 100, 0.1, false)]  // 15% remaining, 10% threshold = not low
    [InlineData(0, 100, 0.1, true)]    // 0% remaining = low
    public void IsQuotaLow_ShouldRespectThreshold(int remaining, int quota, double threshold, bool expectedLow)
    {
        var info = new RateLimitInfo
        {
            Remaining = remaining,
            Quota = quota,
            IsValid = true
        };

        info.IsQuotaLow(threshold).Should().Be(expectedLow);
    }

    [Fact]
    public void IsQuotaLow_WhenNotValid_ShouldReturnFalse()
    {
        var info = new RateLimitInfo
        {
            Remaining = 0,
            Quota = 100,
            IsValid = false
        };

        info.IsQuotaLow().Should().BeFalse();
    }

    [Fact]
    public void IsQuotaLow_WhenQuotaIsZero_ShouldReturnFalse()
    {
        var info = new RateLimitInfo
        {
            Remaining = 0,
            Quota = 0,
            IsValid = true
        };

        info.IsQuotaLow().Should().BeFalse();
    }

    [Fact]
    public void IsQuotaLow_WithDefaultThreshold_ShouldUse10Percent()
    {
        var lowInfo = new RateLimitInfo
        {
            Remaining = 9,
            Quota = 100,
            IsValid = true
        };

        var notLowInfo = new RateLimitInfo
        {
            Remaining = 11,
            Quota = 100,
            IsValid = true
        };

        lowInfo.IsQuotaLow().Should().BeTrue();
        notLowInfo.IsQuotaLow().Should().BeFalse();
    }

    [Fact]
    public void ToString_WhenValid_ShouldReturnFormattedString()
    {
        var info = new RateLimitInfo
        {
            PolicyName = "api-v2",
            Remaining = 50,
            Quota = 100,
            ResetSeconds = 30,
            WindowSeconds = 60,
            IsValid = true
        };

        var result = info.ToString();

        result.Should().Contain("api-v2");
        result.Should().Contain("50/100");
        result.Should().Contain("30s");
        result.Should().Contain("60s");
    }

    [Fact]
    public void ToString_WhenNotValid_ShouldReturnInvalidString()
    {
        var info = default(RateLimitInfo);

        var result = info.ToString();

        result.Should().Contain("Invalid");
    }

    #region CreateFromRetryAfter Tests

    [Fact]
    public void CreateFromRetryAfter_ShouldCreateValidInfo()
    {
        // Act
        var info = RateLimitInfo.CreateFromRetryAfter(60);

        // Assert
        info.IsValid.Should().BeTrue();
        info.PolicyName.Should().Be("retry-after");
        info.Remaining.Should().Be(0);
        info.ResetSeconds.Should().Be(60);
        info.Quota.Should().Be(0);  // Not set from Retry-After
        info.WindowSeconds.Should().Be(0);  // Not set from Retry-After
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(300)]
    [InlineData(3600)]
    public void CreateFromRetryAfter_WithVariousValues_ShouldSetResetSecondsCorrectly(int retryAfterSeconds)
    {
        // Act
        var info = RateLimitInfo.CreateFromRetryAfter(retryAfterSeconds);

        // Assert
        info.ResetSeconds.Should().Be(retryAfterSeconds);
        info.Remaining.Should().Be(0);
        info.IsValid.Should().BeTrue();
    }

    [Fact]
    public void CreateFromRetryAfter_ShouldAlwaysIndicateZeroRemaining()
    {
        // Retry-After implies we've exhausted our quota
        var info = RateLimitInfo.CreateFromRetryAfter(120);

        info.Remaining.Should().Be(0);
        info.IsQuotaLow().Should().BeFalse();  // Quota is 0, so IsQuotaLow returns false
    }

    #endregion

    #region WithRetryAfterOverride Tests

    [Fact]
    public void WithRetryAfterOverride_ShouldOverrideResetSeconds()
    {
        // Arrange
        var original = new RateLimitInfo
        {
            PolicyName = "default",
            Remaining = 50,
            ResetSeconds = 30,
            Quota = 100,
            WindowSeconds = 60,
            IsValid = true
        };

        // Act
        var overridden = original.WithRetryAfterOverride(120);

        // Assert
        overridden.ResetSeconds.Should().Be(120);
        overridden.Remaining.Should().Be(0);
        // Other properties should be preserved
        overridden.PolicyName.Should().Be("default");
        overridden.Quota.Should().Be(100);
        overridden.WindowSeconds.Should().Be(60);
        overridden.IsValid.Should().BeTrue();
    }

    [Fact]
    public void WithRetryAfterOverride_ShouldSetRemainingToZero()
    {
        // Arrange
        var original = new RateLimitInfo
        {
            PolicyName = "burst",
            Remaining = 75,  // Still has remaining quota
            ResetSeconds = 30,
            Quota = 100,
            IsValid = true
        };

        // Act - Retry-After implies we've hit the limit
        var overridden = original.WithRetryAfterOverride(60);

        // Assert
        overridden.Remaining.Should().Be(0);
    }

    [Fact]
    public void WithRetryAfterOverride_ShouldNotMutateOriginal()
    {
        // Arrange
        var original = new RateLimitInfo
        {
            PolicyName = "default",
            Remaining = 50,
            ResetSeconds = 30,
            Quota = 100,
            IsValid = true
        };

        // Act
        _ = original.WithRetryAfterOverride(120);

        // Assert - Original should be unchanged (immutable)
        original.Remaining.Should().Be(50);
        original.ResetSeconds.Should().Be(30);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(300)]
    [InlineData(86400)]  // 24 hours
    public void WithRetryAfterOverride_WithVariousValues_ShouldWork(int retryAfterSeconds)
    {
        // Arrange
        var original = new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 10,
            ResetSeconds = 15,
            Quota = 100,
            IsValid = true
        };

        // Act
        var overridden = original.WithRetryAfterOverride(retryAfterSeconds);

        // Assert
        overridden.ResetSeconds.Should().Be(retryAfterSeconds);
        overridden.Remaining.Should().Be(0);
    }

    [Fact]
    public void WithRetryAfterOverride_OnInvalidInfo_ShouldPreserveInvalidState()
    {
        // Arrange
        var invalid = default(RateLimitInfo);

        // Act
        var overridden = invalid.WithRetryAfterOverride(60);

        // Assert - IsValid remains false because original was invalid
        overridden.IsValid.Should().BeFalse();
        overridden.ResetSeconds.Should().Be(60);
        overridden.Remaining.Should().Be(0);
    }

    #endregion
}
