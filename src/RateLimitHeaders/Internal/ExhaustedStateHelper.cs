using RateLimitHeaders.Parsing;

namespace RateLimitHeaders.Internal;

/// <summary>
/// Shared logic for the wait both call sites (the handler and the Polly strategy) impose
/// when the server ordered a stop: a Retry-After header, or a state with zero remaining
/// requests whose reset moment is still ahead. Enforced above any throttling algorithm.
/// </summary>
internal static class ExhaustedStateHelper
{
    /// <summary>
    /// Whether the state is a server-ordered stop that must be enforced before any
    /// throttling algorithm runs.
    /// </summary>
    public static bool IsServerOrderedStop(RateLimitInfo info) => info.HasRetryAfter || info.IsExhausted;

    /// <summary>
    /// Computes the wait for a server-ordered stop: the exact time until the window resets,
    /// capped at the configured maximum. When the server sent no reset value, the floor is
    /// the window length when one was sent, otherwise
    /// <see cref="RateLimitDefaults.MinimumExhaustedDelay"/> (1 second).
    /// </summary>
    public static TimeSpan GetStopDelay(RateLimitInfo info, TimeSpan timeUntilReset, TimeSpan maxExhaustedDelay)
    {
        var delay = timeUntilReset;
        if (delay <= TimeSpan.Zero)
        {
            delay = info.HasWindowSeconds
                ? TimeSpan.FromSeconds(Math.Min(info.WindowSeconds, RateLimitInfo.MaxResetSeconds))
                : RateLimitDefaults.MinimumExhaustedDelay;
        }

        return delay > maxExhaustedDelay ? maxExhaustedDelay : delay;
    }

    /// <summary>
    /// Builds the throttling reason text for a server-ordered stop. Mentions "Retry-After"
    /// exactly when the state came from a Retry-After header.
    /// </summary>
    public static string GetStopReason(RateLimitInfo info, TimeSpan delay) =>
        info.HasRetryAfter
            ? $"Retry-After: the server ordered a stop; waiting {delay.TotalSeconds:0.###}s"
            : $"Quota exhausted (0 remaining); waiting {delay.TotalSeconds:0.###}s for the window to reset";
}
