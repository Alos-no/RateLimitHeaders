namespace RateLimitHeaders.Internal;

/// <summary>
/// Shared helper methods for rate limit options classes.
/// </summary>
internal static class RateLimitOptionsHelper
{
    /// <summary>
    /// The global state key used when per-endpoint tracking is disabled.
    /// </summary>
    public const string GlobalStateKey = "global";

    /// <summary>
    /// The default state key used when no URI is available.
    /// </summary>
    public const string DefaultStateKey = "default";

    /// <summary>
    /// Gets the state tracking key for a request.
    /// </summary>
    /// <param name="request">The HTTP request.</param>
    /// <param name="trackPerEndpoint">Whether to track state per endpoint.</param>
    /// <param name="customExtractor">Optional custom key extractor function.</param>
    /// <returns>A key identifying the endpoint for state tracking.</returns>
    public static string GetStateKey(
        HttpRequestMessage? request,
        bool trackPerEndpoint,
        Func<HttpRequestMessage, string>? customExtractor)
    {
        if (!trackPerEndpoint || request is null)
        {
            return GlobalStateKey;
        }

        if (customExtractor is not null)
        {
            // A null or blank extractor result falls back to the global key instead of
            // becoming a whitespace key of its own
            var customKey = customExtractor(request);
            return string.IsNullOrWhiteSpace(customKey) ? GlobalStateKey : customKey;
        }

        return GetDefaultStateKey(request);
    }

    /// <summary>
    /// Gets the default state key from a request: scheme, host, and port, so different
    /// ports and schemes of one host never share a rate limit entry (finding AUD-29 in
    /// TRACKER-adversarial-audit.md, the adversarial-audit findings ledger).
    /// </summary>
    /// <param name="request">The HTTP request.</param>
    /// <returns>The key <c>scheme://host:port</c> from the request URI.</returns>
    /// <example>
    /// <c>https://api.example.com/v1/users</c> becomes <c>https://api.example.com:443</c>.
    /// </example>
    public static string GetDefaultStateKey(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return DefaultStateKey;
        }

        return $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}";
    }

    /// <summary>
    /// Validates that a quota threshold is within the valid range.
    /// </summary>
    /// <param name="value">The threshold value to validate.</param>
    /// <param name="parameterName">The parameter name for the exception.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is less than 0.0 or greater than 1.0.
    /// </exception>
    public static void ValidateQuotaThreshold(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0.0 || value > 1.0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "QuotaLowThreshold must be a finite number between 0.0 and 1.0");
        }
    }

    /// <summary>
    /// The largest delay <see cref="Task.Delay(TimeSpan)"/> accepts (uint.MaxValue - 1 milliseconds, about 49.7 days).
    /// </summary>
    public static readonly TimeSpan MaxSupportedDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Validates that a configured delay is non-negative and within what <see cref="Task.Delay(TimeSpan)"/> accepts.
    /// </summary>
    /// <param name="value">The delay value to validate.</param>
    /// <param name="parameterName">The parameter name for the exception.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is negative or exceeds <see cref="MaxSupportedDelay"/>.
    /// </exception>
    public static void ValidateDelay(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero || value > MaxSupportedDelay)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Delay must be between 0 and {MaxSupportedDelay} (the largest value Task.Delay accepts).");
        }
    }
}
