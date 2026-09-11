using Polly;

namespace RateLimitHeaders.Polly;

/// <summary>
/// Convenience extensions for telling the rate limit strategy which endpoint an execution
/// targets, so the pre-request state lookup can find the state written by earlier responses.
/// </summary>
public static class ResilienceContextExtensions
{
    /// <summary>
    /// Sets a caller-chosen state tracking key for this execution. The strategy uses the
    /// string verbatim for both the pre-request lookup and the post-response write.
    /// </summary>
    /// <param name="context">The resilience context of the execution.</param>
    /// <param name="stateKey">The state tracking key, for example <c>"api.example.com"</c>.</param>
    /// <returns>The same context, for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="context"/> or <paramref name="stateKey"/> is null.
    /// </exception>
    public static ResilienceContext SetRateLimitStateKey(this ResilienceContext context, string stateKey)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stateKey);

        context.Properties.Set(RateLimitContextProperties.StateKeyKey, stateKey);
        return context;
    }

    /// <summary>
    /// Stores the outgoing request on the context so the strategy can derive the state
    /// tracking key (<c>scheme://host:port</c>) from its URI before the request is sent.
    /// Equivalent to setting <see cref="RateLimitContextProperties.RequestMessageKey"/> directly.
    /// </summary>
    /// <param name="context">The resilience context of the execution.</param>
    /// <param name="request">The request this execution will send.</param>
    /// <returns>The same context, for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="context"/> or <paramref name="request"/> is null.
    /// </exception>
    public static ResilienceContext SetRateLimitRequest(this ResilienceContext context, HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        context.Properties.Set(RateLimitContextProperties.RequestMessageKey, request);
        return context;
    }
}
