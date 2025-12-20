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
            // Handle null return from custom extractor defensively
            return customExtractor(request) ?? GlobalStateKey;
        }

        return GetDefaultStateKey(request);
    }

    /// <summary>
    /// Gets the default state key from a request (hostname only).
    /// </summary>
    /// <param name="request">The HTTP request.</param>
    /// <returns>The hostname from the request URI.</returns>
    /// <example>
    /// <c>https://api.example.com/v1/users</c> becomes <c>api.example.com</c>.
    /// </example>
    public static string GetDefaultStateKey(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (uri is null)
        {
            return DefaultStateKey;
        }

        return uri.Host;
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
        if (value < 0.0 || value > 1.0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "QuotaLowThreshold must be between 0.0 and 1.0");
        }
    }
}
