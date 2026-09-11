using System.Globalization;
using System.Net;
using RateLimitHeaders.Internal;

namespace RateLimitHeaders.Tests;

/// <summary>
/// Unit tests for RetryAfterParser. The parser produces a <see cref="TimeSpan"/> delay,
/// treats past dates as zero wait, clamps at 30 days, accepts only the three RFC 9110
/// date shapes, and honors the configurable status set (default {403, 408, 429, 503});
/// see the T6/T7 bands in PLAN-audit-fixes.md.
/// </summary>
public class RetryAfterParserTests
{
    private static bool TryGetDelay(HttpResponseMessage response, out TimeSpan delay) =>
        RetryAfterParser.TryGetRetryAfterDelay(response, DateTimeOffset.UtcNow, RateLimitDefaults.RetryAfterStatusCodes, out delay);

    #region Delta-Seconds Format Tests

    [Fact]
    public void TryGetRetryAfterDelay_WithDeltaSeconds_ShouldParseCorrectly()
    {
        // Arrange
        var response = CreateResponse(HttpStatusCode.TooManyRequests, "60");

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeTrue();
        delay.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("30", 30)]
    [InlineData("300", 300)]
    [InlineData("3600", 3600)]
    [InlineData("86400", 86400)]
    public void TryGetRetryAfterDelay_WithVariousDeltaSeconds_ShouldParseCorrectly(string headerValue, int expectedSeconds)
    {
        // Arrange
        var response = CreateResponse(HttpStatusCode.TooManyRequests, headerValue);

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeTrue();
        delay.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void TryGetRetryAfterDelay_WithNegativeSeconds_ShouldReturnFalse()
    {
        // Arrange - a leading sign is not valid delta-seconds and not a valid HTTP-date
        var response = CreateResponse(HttpStatusCode.TooManyRequests, "-5");

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeFalse();
        delay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void TryGetRetryAfterDelay_WithWhitespace_ShouldTrimAndParse()
    {
        // Arrange
        var response = CreateResponse(HttpStatusCode.TooManyRequests, "  60  ");

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeTrue();
        delay.Should().Be(TimeSpan.FromSeconds(60));
    }

    #endregion

    #region HTTP-Date Format Tests

    [Fact]
    public void TryGetRetryAfterDelay_WithHttpDateInFuture_ShouldCalculateDelta()
    {
        // Arrange
        var futureDate = DateTimeOffset.UtcNow.AddSeconds(120);
        var response = CreateResponse(HttpStatusCode.TooManyRequests, futureDate.ToString("R"));

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeTrue();
        // Allow some tolerance for test execution time
        delay.TotalSeconds.Should().BeInRange(118, 122);
    }

    [Fact]
    public void TryGetRetryAfterDelay_WithRfc1123DateFormat_ShouldParse()
    {
        // Arrange - RFC 1123 / IMF-fixdate format: "Wed, 21 Oct 2025 07:28:00 GMT"
        var futureDate = DateTimeOffset.UtcNow.AddSeconds(60);
        var dateString = futureDate.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);
        var response = CreateResponse(HttpStatusCode.TooManyRequests, dateString);

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeTrue();
        delay.TotalSeconds.Should().BeInRange(58, 62);
    }

    [Fact]
    public void TryGetRetryAfterDelay_WithIso8601DateFormat_ShouldReturnFalse()
    {
        // Arrange - ISO 8601 is not one of the three RFC 9110 date shapes, so the
        // format-exact parse rejects it (the old DateTimeOffset.TryParse accepted it)
        var futureDate = DateTimeOffset.UtcNow.AddMinutes(5);
        var response = CreateResponse(HttpStatusCode.TooManyRequests, futureDate.ToString("o"));

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeFalse();
        delay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void TryGetRetryAfterDelay_WithDateInPast_ShouldReturnZeroWait()
    {
        // Arrange - a past date is a valid header meaning "you may send now"; reporting
        // it as "no header" suppressed the precedence override over RateLimit values
        var pastDate = DateTimeOffset.UtcNow.AddMinutes(-5);
        var response = CreateResponse(HttpStatusCode.TooManyRequests, pastDate.ToString("R"));

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeTrue();
        delay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void TryGetRetryAfterDelay_WithExactNow_ShouldReturnZeroWait()
    {
        // Arrange - a date equal to (or a moment before) now means zero wait
        var nowDate = DateTimeOffset.UtcNow;
        var response = CreateResponse(HttpStatusCode.TooManyRequests, nowDate.ToString("R"));

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeTrue();
        delay.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1));
    }

    #endregion

    #region Status Code Tests

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void TryGetRetryAfterDelay_WithHonoredStatusCodes_ShouldParse(HttpStatusCode statusCode)
    {
        // Arrange - the default honored set is {403, 408, 429, 503}
        var response = CreateResponse(statusCode, "60");

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeTrue();
        delay.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public void TryGetRetryAfterDelay_WithOtherStatusCodes_ShouldReturnFalse(HttpStatusCode statusCode)
    {
        // Arrange
        var response = CreateResponse(statusCode, "60");

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeFalse();
        delay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void TryGetRetryAfterDelay_WithCallerConfiguredStatusSet_ShouldConsultIt()
    {
        // Arrange - a caller-supplied set replaces the default entirely
        var response = CreateResponse(HttpStatusCode.OK, "30");

        // Act
        var result = RetryAfterParser.TryGetRetryAfterDelay(response, DateTimeOffset.UtcNow, [200], out var delay);

        // Assert
        result.Should().BeTrue();
        delay.Should().Be(TimeSpan.FromSeconds(30));
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void TryGetRetryAfterDelay_WithMissingHeader_ShouldReturnFalse()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        // No Retry-After header added

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeFalse();
        delay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void TryGetRetryAfterDelay_WithEmptyHeader_ShouldReturnFalse()
    {
        // Arrange
        var response = CreateResponse(HttpStatusCode.TooManyRequests, "");

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryGetRetryAfterDelay_WithWhitespaceOnlyHeader_ShouldReturnFalse()
    {
        // Arrange
        var response = CreateResponse(HttpStatusCode.TooManyRequests, "   ");

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryGetRetryAfterDelay_WithInvalidValue_ShouldReturnFalse()
    {
        // Arrange
        var response = CreateResponse(HttpStatusCode.TooManyRequests, "not-a-number-or-date");

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryGetRetryAfterDelay_WithDecimalSeconds_ShouldReturnFalse()
    {
        // Arrange - decimal values are not valid delta-seconds, and "60.5" is not a date
        var response = CreateResponse(HttpStatusCode.TooManyRequests, "60.5");

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryGetRetryAfterDelay_WithVeryLargeFutureDelta_ShouldCalculateCorrectly()
    {
        // Arrange - 1 hour in the future
        var futureDate = DateTimeOffset.UtcNow.AddHours(1);
        var response = CreateResponse(HttpStatusCode.TooManyRequests, futureDate.ToString("R"));

        // Act
        var result = TryGetDelay(response, out var delay);

        // Assert
        result.Should().BeTrue();
        delay.TotalSeconds.Should().BeInRange(3598, 3602);
    }

    #endregion

    #region Helper Methods

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string retryAfterValue)
    {
        var response = new HttpResponseMessage(statusCode);
        if (!string.IsNullOrEmpty(retryAfterValue))
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfterValue);
        }
        return response;
    }

    #endregion

    #region Culture-Invariant Parsing Tests

    [Fact]
    public void TryGetRetryAfterDelay_WithGermanCulture_ShouldStillParseHttpDate()
    {
        // Arrange - German culture uses different date formats
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            // HTTP-date format is culture-invariant (RFC 9110)
            var futureDate = DateTimeOffset.UtcNow.AddSeconds(120);
            var response = CreateResponse(HttpStatusCode.TooManyRequests, futureDate.ToString("R"));

            // Act
            var result = TryGetDelay(response, out var delay);

            // Assert - Should still parse correctly despite German culture
            result.Should().BeTrue();
            delay.TotalSeconds.Should().BeInRange(118, 122);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void TryGetRetryAfterDelay_WithFrenchCulture_ShouldStillParseHttpDate()
    {
        // Arrange - French culture uses different date formats
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

            var futureDate = DateTimeOffset.UtcNow.AddSeconds(60);
            var response = CreateResponse(HttpStatusCode.TooManyRequests, futureDate.ToString("R"));

            // Act
            var result = TryGetDelay(response, out var delay);

            // Assert
            result.Should().BeTrue();
            delay.TotalSeconds.Should().BeInRange(58, 62);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void TryGetRetryAfterDelay_WithArabicCulture_ShouldStillParseDeltaSeconds()
    {
        // Arrange - Arabic culture uses different number formats
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");

            // Delta-seconds should still parse as an integer
            var response = CreateResponse(HttpStatusCode.TooManyRequests, "120");

            // Act
            var result = TryGetDelay(response, out var delay);

            // Assert
            result.Should().BeTrue();
            delay.Should().Be(TimeSpan.FromSeconds(120));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void TryGetRetryAfterDelay_WithJapaneseCulture_ShouldStillParseHttpDate()
    {
        // Arrange - Japanese culture uses different date formats
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ja-JP");

            var futureDate = DateTimeOffset.UtcNow.AddMinutes(2);
            var response = CreateResponse(HttpStatusCode.TooManyRequests, futureDate.ToString("R"));

            // Act
            var result = TryGetDelay(response, out var delay);

            // Assert
            result.Should().BeTrue();
            delay.TotalSeconds.Should().BeInRange(118, 122);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ja-JP")]
    [InlineData("zh-CN")]
    [InlineData("ru-RU")]
    [InlineData("ar-SA")]
    public void TryGetRetryAfterDelay_WithVariousCultures_ShouldParseConsistently(string cultureName)
    {
        // Arrange
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);

            var futureDate = DateTimeOffset.UtcNow.AddSeconds(300);
            var response = CreateResponse(HttpStatusCode.TooManyRequests, futureDate.ToString("R"));

            // Act
            var result = TryGetDelay(response, out var delay);

            // Assert - Should work consistently across all cultures
            result.Should().BeTrue($"Should parse in culture {cultureName}");
            delay.TotalSeconds.Should().BeInRange(298, 302, $"Should calculate correct delta in culture {cultureName}");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    #endregion
}
