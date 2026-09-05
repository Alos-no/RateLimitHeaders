using RateLimitHeaders.Parsing;

namespace RateLimitHeaders.Throttling;

/// <summary>
/// Defines a throttling algorithm that determines whether and how long to delay requests
/// based on rate limit information.
/// </summary>
/// <remarks>
/// <para>
/// Implementations of this interface provide different strategies for proactive
/// client-side throttling. The goal is to slow down requests before hitting 429
/// errors by analyzing rate limit header information.
/// </para>
/// <para>
/// Built-in implementations:
/// <list type="bullet">
/// <item><see cref="PercentageThrottlingAlgorithm"/> - Simple threshold-based throttling</item>
/// </list>
/// </para>
/// <para>
/// Future implementations may include Google SRE-style adaptive throttling
/// which tracks request/accept ratios over a sliding window.
/// </para>
/// </remarks>
/// <example>
/// <para>Basic custom throttling algorithm:</para>
/// <code><![CDATA[
/// public class FixedDelayThrottlingAlgorithm : IThrottlingAlgorithm
/// {
///     private readonly TimeSpan _fixedDelay;
///     private readonly double _threshold;
///
///     public FixedDelayThrottlingAlgorithm(TimeSpan delay, double threshold = 0.1)
///     {
///         _fixedDelay = delay;
///         _threshold = threshold;
///     }
///
///     public ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo)
///     {
///         if (!rateLimitInfo.IsValid || rateLimitInfo.Quota <= 0)
///             return ThrottlingResult.NoThrottle;
///
///         if (rateLimitInfo.GetRemainingPercentage() >= _threshold)
///             return ThrottlingResult.NoThrottle;
///
///         return ThrottlingResult.Throttle(_fixedDelay, "Below quota threshold");
///     }
/// }
/// ]]></code>
/// <para>Advanced algorithm using state tracker for cross-endpoint decisions:</para>
/// <code><![CDATA[
/// public class GlobalThrottlingAlgorithm : IThrottlingAlgorithm
/// {
///     public ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo) =>
///         Evaluate(rateLimitInfo, null);
///
///     public ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo, IRateLimitStateProvider? stateProvider)
///     {
///         if (stateProvider == null)
///             return DefaultEvaluate(rateLimitInfo);
///
///         // Access other endpoint states for global throttling decisions
///         var allStates = stateProvider.GetAllStates();
///         var lowestQuota = allStates.Min(s => s.GetRemainingPercentage());
///         // ... implement global throttling logic
///     }
/// }
/// ]]></code>
/// </example>
public interface IThrottlingAlgorithm
{
    /// <summary>
    /// Evaluates the current rate limit state and determines whether throttling is needed.
    /// </summary>
    /// <param name="rateLimitInfo">The parsed rate limit information from the most recent response.</param>
    /// <returns>A <see cref="ThrottlingResult"/> indicating whether to throttle and for how long.</returns>
    ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo);

    /// <summary>
    /// Evaluates the current rate limit state and determines whether throttling is needed,
    /// with access to the full rate limit state across all tracked endpoints.
    /// </summary>
    /// <param name="rateLimitInfo">The parsed rate limit information from the most recent response.</param>
    /// <param name="stateProvider">
    /// Optional provider for accessing rate limit state across all tracked endpoints.
    /// Useful for implementing global throttling strategies that consider the overall API health.
    /// </param>
    /// <returns>A <see cref="ThrottlingResult"/> indicating whether to throttle and for how long.</returns>
    /// <remarks>
    /// The default implementation ignores the state provider and delegates to <see cref="Evaluate(RateLimitInfo)"/>.
    /// Override this method when implementing algorithms that need cross-endpoint visibility.
    /// </remarks>
    ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo, IRateLimitStateProvider? stateProvider)
    {
        // Default implementation ignores state provider
        return Evaluate(rateLimitInfo);
    }

    /// <summary>
    /// Evaluates a time-adjusted throttling context and determines whether throttling is needed.
    /// </summary>
    /// <param name="context">
    /// The time-adjusted state: <see cref="ThrottlingContext.RateLimitInfo"/> already has the
    /// elapsed time since the response arrived subtracted from its reset value, and the context
    /// carries the observation instant and the exact time until the window resets.
    /// </param>
    /// <returns>A <see cref="ThrottlingResult"/> indicating whether to throttle and for how long.</returns>
    /// <remarks>
    /// The default implementation delegates to
    /// <see cref="Evaluate(RateLimitInfo, IRateLimitStateProvider?)"/> with the adjusted info,
    /// so existing algorithms keep working unchanged. Override this method to use the timing detail.
    /// The handler enforces a server-ordered stop (a Retry-After, or zero remaining requests)
    /// before consulting any algorithm; an algorithm cannot bypass that wait.
    /// </remarks>
    ThrottlingResult Evaluate(ThrottlingContext context)
    {
        return Evaluate(context.RateLimitInfo, context.StateProvider);
    }
}

/// <summary>
/// Provides read-only access to rate limit state information across all tracked endpoints.
/// </summary>
/// <remarks>
/// This interface is passed to throttling algorithms that need to make decisions
/// based on the overall rate limit state, not just a single endpoint.
/// </remarks>
public interface IRateLimitStateProvider
{
    /// <summary>
    /// Gets the rate limit information for a specific endpoint.
    /// </summary>
    /// <param name="endpointKey">The endpoint key (e.g., "api.example.com/v1").</param>
    /// <returns>The rate limit info, or a default invalid instance if not tracked.</returns>
    RateLimitInfo GetRateLimitInfo(string endpointKey);

    /// <summary>
    /// Gets all tracked endpoint keys.
    /// </summary>
    IEnumerable<string> TrackedEndpoints { get; }

    /// <summary>
    /// Gets the rate limit information for all tracked endpoints.
    /// </summary>
    /// <returns>An enumerable of all currently tracked rate limit states.</returns>
    IEnumerable<RateLimitInfo> GetAllStates();
}
