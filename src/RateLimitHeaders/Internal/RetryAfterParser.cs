using System.Globalization;
using System.Net;

namespace RateLimitHeaders.Internal;

/// <summary>
/// Utility for parsing Retry-After headers from HTTP responses.
/// </summary>
internal static class RetryAfterParser
{
    /// <summary>
    /// Attempts to extract the Retry-After value from a response.
    /// </summary>
    /// <param name="response">The HTTP response to examine.</param>
    /// <param name="seconds">When successful, the number of seconds to wait.</param>
    /// <returns>True if a valid Retry-After value was found; otherwise false.</returns>
    /// <remarks>
    /// Only checks for Retry-After on 429 (TooManyRequests) or 503 (ServiceUnavailable) responses.
    /// Supports both delta-seconds and HTTP-date formats per RFC 7231.
    /// </remarks>
    public static bool TryGetRetryAfterSeconds(HttpResponseMessage response, out int seconds)
    {
        seconds = 0;

        // Only check for Retry-After on 429 or 503 responses
        if (response.StatusCode != HttpStatusCode.TooManyRequests &&
            response.StatusCode != HttpStatusCode.ServiceUnavailable)
        {
            return false;
        }

        // Try to get Retry-After header
        if (!response.Headers.TryGetValues("Retry-After", out var values))
        {
            return false;
        }

        var retryAfterValue = values.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(retryAfterValue))
        {
            return false;
        }

        // Retry-After can be delta-seconds or HTTP-date
        // First try parsing as integer (delta-seconds)
        if (int.TryParse(retryAfterValue, out var parsedSeconds) && parsedSeconds >= 0)
        {
            seconds = parsedSeconds;
            return true;
        }

        // Try parsing as HTTP-date format (RFC 7231)
        // HTTP-date is always in GMT/UTC, so use InvariantCulture and AssumeUniversal
        if (DateTimeOffset.TryParse(
            retryAfterValue,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var retryDate))
        {
            var delta = retryDate - DateTimeOffset.UtcNow;
            if (delta > TimeSpan.Zero)
            {
                seconds = (int)Math.Ceiling(delta.TotalSeconds);
                return true;
            }
        }

        return false;
    }
}
