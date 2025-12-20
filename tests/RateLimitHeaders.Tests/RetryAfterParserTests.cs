using System.Globalization;
using System.Net;
using RateLimitHeaders.Internal;

namespace RateLimitHeaders.Tests;

/// <summary>
/// Unit tests for RetryAfterParser.
/// </summary>
public class RetryAfterParserTests
{
    #region Delta-Seconds Format Tests

    [Fact]
    public void TryGetRetryAfterSeconds_WithDeltaSeconds_ShouldParseCorrectly()
    {
        // Arrange
        var response = CreateResponse(HttpStatusCode.TooManyRequests, "60");

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        result.Should().BeTrue();
        seconds.Should().Be(60);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("30", 30)]
    [InlineData("300", 300)]
    [InlineData("3600", 3600)]
    [InlineData("86400", 86400)]
    public void TryGetRetryAfterSeconds_WithVariousDeltaSeconds_ShouldParseCorrectly(string headerValue, int expectedSeconds)
    {
        // Arrange
        var response = CreateResponse(HttpStatusCode.TooManyRequests, headerValue);

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        result.Should().BeTrue();
        seconds.Should().Be(expectedSeconds);
    }

    [Fact]
    public void TryGetRetryAfterSeconds_WithNegativeSeconds_ShouldReturnFalse()
    {
        // Arrange
        var response = CreateResponse(HttpStatusCode.TooManyRequests, "-5");

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        result.Should().BeFalse();
        seconds.Should().Be(0);
    }

    [Fact]
    public void TryGetRetryAfterSeconds_WithWhitespace_ShouldTrimAndParse()
    {
        // Arrange
        var response = CreateResponse(HttpStatusCode.TooManyRequests, "  60  ");

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        result.Should().BeTrue();
        seconds.Should().Be(60);
    }

    #endregion

    #region HTTP-Date Format Tests

    [Fact]
    public void TryGetRetryAfterSeconds_WithHttpDateInFuture_ShouldCalculateDelta()
    {
        // Arrange
        var futureDate = DateTimeOffset.UtcNow.AddSeconds(120);
        var response = CreateResponse(HttpStatusCode.TooManyRequests, futureDate.ToString("R"));

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        result.Should().BeTrue();
        // Allow some tolerance for test execution time
        seconds.Should().BeInRange(118, 122);
    }

    [Fact]
    public void TryGetRetryAfterSeconds_WithRfc1123DateFormat_ShouldParse()
    {
        // Arrange - RFC 1123 format: "Wed, 21 Oct 2025 07:28:00 GMT"
        var futureDate = DateTimeOffset.UtcNow.AddSeconds(60);
        var dateString = futureDate.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", System.Globalization.CultureInfo.InvariantCulture);
        var response = CreateResponse(HttpStatusCode.TooManyRequests, dateString);

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        result.Should().BeTrue();
        seconds.Should().BeInRange(58, 62);
    }

    [Fact]
    public void TryGetRetryAfterSeconds_WithIso8601DateFormat_ShouldParse()
    {
        // Arrange - ISO 8601 format
        var futureDate = DateTimeOffset.UtcNow.AddMinutes(5);
        var dateString = futureDate.ToString("o");  // ISO 8601
        var response = CreateResponse(HttpStatusCode.TooManyRequests, dateString);

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        result.Should().BeTrue();
        seconds.Should().BeInRange(298, 302);  // ~5 minutes in seconds
    }

    [Fact]
    public void TryGetRetryAfterSeconds_WithDateInPast_ShouldReturnFalse()
    {
        // Arrange - Date in the past
        var pastDate = DateTimeOffset.UtcNow.AddMinutes(-5);
        var response = CreateResponse(HttpStatusCode.TooManyRequests, pastDate.ToString("R"));

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        result.Should().BeFalse();
        seconds.Should().Be(0);
    }

    [Fact]
    public void TryGetRetryAfterSeconds_WithExactNow_ShouldReturnFalse()
    {
        // Arrange - Date exactly now (delta would be 0 or slightly negative)
        var nowDate = DateTimeOffset.UtcNow;
        var response = CreateResponse(HttpStatusCode.TooManyRequests, nowDate.ToString("R"));

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert - TimeSpan.Zero is not > TimeSpan.Zero, so should return false
        result.Should().BeFalse();
    }

    #endregion

    #region Status Code Tests

    [Fact]
    public void TryGetRetryAfterSeconds_With429StatusCode_ShouldParse()
    {
        // Arrange
        var response = CreateResponse(HttpStatusCode.TooManyRequests, "60");

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        result.Should().BeTrue();
        seconds.Should().Be(60);
    }

    [Fact]
    public void TryGetRetryAfterSeconds_With503StatusCode_ShouldParse()
    {
        // Arrange
        var response = CreateResponse(HttpStatusCode.ServiceUnavailable, "120");

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        result.Should().BeTrue();
        seconds.Should().Be(120);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public void TryGetRetryAfterSeconds_WithOtherStatusCodes_ShouldReturnFalse(HttpStatusCode statusCode)
    {
        // Arrange
        var response = CreateResponse(statusCode, "60");

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        result.Should().BeFalse();
        seconds.Should().Be(0);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void TryGetRetryAfterSeconds_WithMissingHeader_ShouldReturnFalse()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        // No Retry-After header added

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        result.Should().BeFalse();
        seconds.Should().Be(0);
    }

    [Fact]
    public void TryGetRetryAfterSeconds_WithEmptyHeader_ShouldReturnFalse()
    {
        // Arrange
        var response = CreateResponse(HttpStatusCode.TooManyRequests, "");

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryGetRetryAfterSeconds_WithWhitespaceOnlyHeader_ShouldReturnFalse()
    {
        // Arrange
        var response = CreateResponse(HttpStatusCode.TooManyRequests, "   ");

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryGetRetryAfterSeconds_WithInvalidValue_ShouldReturnFalse()
    {
        // Arrange
        var response = CreateResponse(HttpStatusCode.TooManyRequests, "not-a-number-or-date");

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryGetRetryAfterSeconds_WithDecimalSeconds_ShouldReturnFalse()
    {
        // Arrange - Decimal values are not valid delta-seconds
        var response = CreateResponse(HttpStatusCode.TooManyRequests, "60.5");

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        // Note: 60.5 is not a valid int, and it's also not a valid date format,
        // so this should return false
        result.Should().BeFalse();
    }

    [Fact]
    public void TryGetRetryAfterSeconds_WithVeryLargeFutureDelta_ShouldCalculateCorrectly()
    {
        // Arrange - 1 hour in the future
        var futureDate = DateTimeOffset.UtcNow.AddHours(1);
        var response = CreateResponse(HttpStatusCode.TooManyRequests, futureDate.ToString("R"));

        // Act
        var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

        // Assert
        result.Should().BeTrue();
        seconds.Should().BeInRange(3598, 3602);  // ~3600 seconds with some tolerance
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
    public void TryGetRetryAfterSeconds_WithGermanCulture_ShouldStillParseHttpDate()
    {
        // Arrange - German culture uses different date formats
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            // HTTP-date format is culture-invariant (RFC 7231)
            var futureDate = DateTimeOffset.UtcNow.AddSeconds(120);
            var httpDateString = futureDate.ToString("R"); // RFC 1123 format
            var response = CreateResponse(HttpStatusCode.TooManyRequests, httpDateString);

            // Act
            var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

            // Assert - Should still parse correctly despite German culture
            result.Should().BeTrue();
            seconds.Should().BeInRange(118, 122);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void TryGetRetryAfterSeconds_WithFrenchCulture_ShouldStillParseHttpDate()
    {
        // Arrange - French culture uses different date formats
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

            var futureDate = DateTimeOffset.UtcNow.AddSeconds(60);
            var httpDateString = futureDate.ToString("R");
            var response = CreateResponse(HttpStatusCode.TooManyRequests, httpDateString);

            // Act
            var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

            // Assert
            result.Should().BeTrue();
            seconds.Should().BeInRange(58, 62);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void TryGetRetryAfterSeconds_WithArabicCulture_ShouldStillParseDeltaSeconds()
    {
        // Arrange - Arabic culture uses different number formats
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");

            // Delta-seconds should still parse as integer
            var response = CreateResponse(HttpStatusCode.TooManyRequests, "120");

            // Act
            var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

            // Assert
            result.Should().BeTrue();
            seconds.Should().Be(120);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void TryGetRetryAfterSeconds_WithJapaneseCulture_ShouldStillParseHttpDate()
    {
        // Arrange - Japanese culture uses different date formats
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ja-JP");

            var futureDate = DateTimeOffset.UtcNow.AddMinutes(2);
            var httpDateString = futureDate.ToString("R");
            var response = CreateResponse(HttpStatusCode.TooManyRequests, httpDateString);

            // Act
            var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

            // Assert
            result.Should().BeTrue();
            seconds.Should().BeInRange(118, 122);
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
    public void TryGetRetryAfterSeconds_WithVariousCultures_ShouldParseConsistently(string cultureName)
    {
        // Arrange
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);

            var futureDate = DateTimeOffset.UtcNow.AddSeconds(300);
            var httpDateString = futureDate.ToString("R"); // RFC 1123 format
            var response = CreateResponse(HttpStatusCode.TooManyRequests, httpDateString);

            // Act
            var result = RetryAfterParser.TryGetRetryAfterSeconds(response, out var seconds);

            // Assert - Should work consistently across all cultures
            result.Should().BeTrue($"Should parse in culture {cultureName}");
            seconds.Should().BeInRange(298, 302, $"Should calculate correct delta in culture {cultureName}");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    #endregion
}
