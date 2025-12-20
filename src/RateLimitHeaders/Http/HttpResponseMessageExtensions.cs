using RateLimitHeaders.Parsing;

namespace RateLimitHeaders.Http;

/// <summary>
/// Extension methods for accessing rate limit information from HTTP responses.
/// </summary>
public static class HttpResponseMessageExtensions
{
    /// <summary>
    /// Key used to store rate limit info in HttpRequestMessage.Options.
    /// </summary>
    private const string RateLimitInfoOptionsKey = "RateLimitHeaders.RateLimitInfo";

    /// <summary>
    /// Gets the parsed rate limit information from the response headers.
    /// </summary>
    /// <param name="response">The HTTP response message.</param>
    /// <returns>
    /// A <see cref="RateLimitInfo"/> with parsed values.
    /// Check <see cref="RateLimitInfo.IsValid"/> to determine if parsing was successful.
    /// </returns>
    /// <remarks>
    /// This method parses the response headers on each call. For repeated access,
    /// consider using <see cref="TryGetRateLimitInfo"/> and caching the result.
    /// </remarks>
    /// <example>
    /// <code><![CDATA[
    /// var response = await httpClient.GetAsync("/api/resource");
    /// var info = response.GetRateLimitInfo();
    ///
    /// if (info.IsValid)
    /// {
    ///     Console.WriteLine($"Remaining: {info.Remaining}/{info.Quota}");
    /// }
    /// ]]></code>
    /// </example>
    public static RateLimitInfo GetRateLimitInfo(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return RateLimitHeaderParser.Parse(response);
    }

    /// <summary>
    /// Attempts to get the parsed rate limit information from the response headers.
    /// </summary>
    /// <param name="response">The HTTP response message.</param>
    /// <param name="rateLimitInfo">
    /// When this method returns <c>true</c>, contains the parsed rate limit information.
    /// </param>
    /// <returns><c>true</c> if rate limit headers were successfully parsed; otherwise, <c>false</c>.</returns>
    /// <example>
    /// <code><![CDATA[
    /// var response = await httpClient.GetAsync("/api/resource");
    ///
    /// if (response.TryGetRateLimitInfo(out var info))
    /// {
    ///     Console.WriteLine($"Policy: {info.PolicyName}");
    ///     Console.WriteLine($"Remaining: {info.Remaining}/{info.Quota}");
    ///
    ///     if (info.IsQuotaLow(0.1))
    ///     {
    ///         Console.WriteLine("Warning: Quota is low!");
    ///     }
    /// }
    /// ]]></code>
    /// </example>
    public static bool TryGetRateLimitInfo(this HttpResponseMessage response, out RateLimitInfo rateLimitInfo)
    {
        ArgumentNullException.ThrowIfNull(response);
        return RateLimitHeaderParser.TryParse(response, out rateLimitInfo);
    }

    /// <summary>
    /// Sets the rate limit info in the request's options for later retrieval.
    /// </summary>
    /// <param name="request">The HTTP request message.</param>
    /// <param name="rateLimitInfo">The rate limit info to store.</param>
    /// <remarks>
    /// This is used internally by the <see cref="RateLimitAwareHandler"/> to store
    /// parsed rate limit info from the response in the request options, allowing
    /// callers to retrieve it after the request completes.
    /// </remarks>
    internal static void SetRateLimitInfo(this HttpRequestMessage request, RateLimitInfo rateLimitInfo)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Options.Set(new HttpRequestOptionsKey<RateLimitInfo>(RateLimitInfoOptionsKey), rateLimitInfo);
    }

    /// <summary>
    /// Gets the rate limit info that was stored in the request's options during response processing.
    /// </summary>
    /// <param name="response">The HTTP response message.</param>
    /// <param name="rateLimitInfo">
    /// When this method returns <c>true</c>, contains the rate limit info that was stored during response processing.
    /// </param>
    /// <returns><c>true</c> if rate limit info was found in the request options; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// This retrieves rate limit info that was stored by the <see cref="RateLimitAwareHandler"/>
    /// during response processing. This is useful when the response object has been passed through
    /// multiple handlers and you want to access the rate limit info without re-parsing.
    /// </remarks>
    /// <example>
    /// <code><![CDATA[
    /// // When using RateLimitAwareHandler, rate limit info is automatically stored
    /// var response = await httpClient.GetAsync("/api/resource");
    ///
    /// if (response.TryGetStoredRateLimitInfo(out var info))
    /// {
    ///     Console.WriteLine($"Cached info: {info.Remaining}/{info.Quota}");
    /// }
    /// ]]></code>
    /// </example>
    public static bool TryGetStoredRateLimitInfo(this HttpResponseMessage response, out RateLimitInfo rateLimitInfo)
    {
        ArgumentNullException.ThrowIfNull(response);
        rateLimitInfo = default;

        if (response.RequestMessage is null)
        {
            return false;
        }

        if (response.RequestMessage.Options.TryGetValue(
            new HttpRequestOptionsKey<RateLimitInfo>(RateLimitInfoOptionsKey),
            out var storedInfo))
        {
            rateLimitInfo = storedInfo;
            return storedInfo.IsValid;
        }

        return false;
    }
}
