namespace RateLimitHeaders.Polly;

/// <summary>
/// Reports which rung of the strategy's state key resolution chain produced the key a
/// throttling decision was based on. Carried on <see cref="OnThrottlingArguments.Source"/>.
/// </summary>
public enum ThrottleDecisionSource
{
    /// <summary>No throttling decision was made, or the source is unknown.</summary>
    None = 0,

    /// <summary>
    /// Per-endpoint tracking is disabled
    /// (<see cref="RateLimitHeadersStrategyOptions.TrackStatePerEndpoint"/> is false),
    /// so the single global entry drove the decision.
    /// </summary>
    GlobalTracking = 1,

    /// <summary>
    /// The caller set a key string on the context
    /// (<see cref="RateLimitContextProperties.StateKeyKey"/>, typically via
    /// <see cref="ResilienceContextExtensions.SetRateLimitStateKey"/>); that string was used
    /// verbatim for both the lookup and the response write.
    /// </summary>
    RequestStateKey = 2,

    /// <summary>
    /// The caller stored the outgoing request on the context
    /// (<see cref="RateLimitContextProperties.RequestMessageKey"/>, typically via
    /// <see cref="ResilienceContextExtensions.SetRateLimitRequest"/>); the key was derived
    /// from that request's URI.
    /// </summary>
    RequestMessage = 3,

    /// <summary>
    /// The key was derived from the request that <c>Microsoft.Extensions.Http.Resilience</c>
    /// publishes on the context under the property name <c>Resilience.Http.RequestMessage</c>,
    /// so the standard resilience handler integration works with no caller code.
    /// </summary>
    ResilienceRequestMessage = 4,

    /// <summary>
    /// No key could be resolved before the request, so the strategy throttled from the state
    /// of the endpoint it most recently observed a response from
    /// (<see cref="RateLimitHeadersStrategyOptions.ThrottleWhenStateKeyUnknown"/>).
    /// </summary>
    LastObservedEndpoint = 5,
}
