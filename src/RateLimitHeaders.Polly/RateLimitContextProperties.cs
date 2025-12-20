using Polly;
using RateLimitHeaders.Parsing;

namespace RateLimitHeaders.Polly;

/// <summary>
/// Contains ResilienceContext property keys for rate limit information.
/// </summary>
/// <example>
/// <para>Accessing rate limit info after executing a request:</para>
/// <code><![CDATA[
/// var context = ResilienceContextPool.Shared.Get();
/// try
/// {
///     var response = await pipeline.ExecuteAsync(
///         async (ctx) => await httpClient.GetAsync("https://api.example.com/v1/data"),
///         context);
///
///     if (context.Properties.TryGetValue(RateLimitContextProperties.RateLimitInfoKey, out RateLimitInfo info))
///     {
///         Console.WriteLine($"Remaining: {info.Remaining}/{info.Quota}");
///     }
/// }
/// finally
/// {
///     ResilienceContextPool.Shared.Return(context);
/// }
/// ]]></code>
/// </example>
public static class RateLimitContextProperties
{
    /// <summary>
    /// The key used to store rate limit info in the <see cref="ResilienceContext.Properties"/>.
    /// </summary>
    /// <remarks>
    /// After executing a request through a resilience pipeline with rate limit header parsing,
    /// you can retrieve the parsed rate limit info using this key.
    /// </remarks>
    /// <example>
    /// <code><![CDATA[
    /// context.Properties.TryGetValue(RateLimitContextProperties.RateLimitInfoKey, out RateLimitInfo rateLimitInfo);
    /// ]]></code>
    /// </example>
    public static readonly ResiliencePropertyKey<RateLimitInfo> RateLimitInfoKey =
        new("RateLimitHeaders.RateLimitInfo");

    /// <summary>
    /// The key used to store the HTTP request message in the <see cref="ResilienceContext.Properties"/>.
    /// </summary>
    /// <remarks>
    /// When using per-endpoint rate limit tracking, set the request message in the context
    /// before executing the pipeline to enable proactive throttling based on the target endpoint.
    /// If not set, the strategy will fall back to using response.RequestMessage for tracking
    /// (which only affects post-response state updates, not proactive throttling).
    /// </remarks>
    /// <example>
    /// <code><![CDATA[
    /// var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v1/users");
    /// context.Properties.Set(RateLimitContextProperties.RequestMessageKey, request);
    /// ]]></code>
    /// </example>
    public static readonly ResiliencePropertyKey<HttpRequestMessage> RequestMessageKey =
        new("RateLimitHeaders.RequestMessage");
}
