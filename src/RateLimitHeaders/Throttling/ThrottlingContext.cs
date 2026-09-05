using RateLimitHeaders.Parsing;

namespace RateLimitHeaders.Throttling;

/// <summary>
/// The time-adjusted rate limit state handed to a throttling decision.
/// </summary>
/// <param name="RateLimitInfo">
/// The stored state adjusted for elapsed time: <see cref="Parsing.RateLimitInfo.ResetSeconds"/>
/// reflects the seconds still left in the window at the moment of the read, not the value the
/// server sent when the response arrived.
/// </param>
/// <param name="ObservedAt">The instant the response that produced this state arrived.</param>
/// <param name="TimeSinceObserved">The time elapsed between <paramref name="ObservedAt"/> and this read.</param>
/// <param name="TimeUntilReset">
/// The exact (sub-second) time left until the window's reset moment;
/// <see cref="TimeSpan.Zero"/> when the server sent no reset value.
/// </param>
/// <param name="StateProvider">
/// Optional read access to the state of all tracked endpoints, for algorithms that make
/// cross-endpoint decisions.
/// </param>
public readonly record struct ThrottlingContext(
    RateLimitInfo RateLimitInfo,
    DateTimeOffset ObservedAt,
    TimeSpan TimeSinceObserved,
    TimeSpan TimeUntilReset,
    IRateLimitStateProvider? StateProvider);
