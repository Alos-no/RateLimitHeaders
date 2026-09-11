namespace RateLimitHeaders;

/// <summary>
/// Library-wide default values shared by both packages.
/// </summary>
public static class RateLimitDefaults
{
    /// <summary>
    /// The default cap on the wait imposed when the server ordered a stop
    /// (a Retry-After, or zero remaining with the reset moment still ahead): 5 minutes.
    /// </summary>
    /// <remarks>
    /// This cap can exceed <see cref="HttpClient.Timeout"/> (100 seconds by default) and the
    /// 30-second total-request timeout that <c>AddStandardResilienceHandler</c> configures.
    /// A wait longer than the ambient timeout surfaces as a <see cref="TaskCanceledException"/>
    /// from that layer. Raise the timeout or lower this cap to fit your timeout budget.
    /// </remarks>
    public static readonly TimeSpan DefaultMaxExhaustedDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The wait applied when the server reported zero remaining requests but sent neither
    /// a reset value nor a window length: 1 second. Without any floor, the draft's minimal
    /// legal shape (<c>RateLimit: "default";r=0</c>) would produce a zero-length wait and
    /// the client would send immediately into a reported stop.
    /// </summary>
    public static readonly TimeSpan MinimumExhaustedDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The response status codes on which a Retry-After header is honored by default:
    /// 403 (secondary limits, as GitHub sends), 408 (request timeout), 429 (too many
    /// requests), and 503 (service unavailable). Retry-After on other statuses (a 3xx
    /// redirect, for example) times something other than a rate limit and is ignored.
    /// </summary>
    public static IReadOnlyList<int> RetryAfterStatusCodes { get; } = [403, 408, 429, 503];
}
