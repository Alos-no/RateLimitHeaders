using Polly;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Throttling;

namespace RateLimitHeaders.Polly;

/// <summary>
/// Options for configuring the rate limit headers resilience strategy.
/// </summary>
/// <example>
/// <para>Basic usage with Polly ResiliencePipelineBuilder:</para>
/// <code><![CDATA[
/// var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
///     .AddRateLimitHeaders(options =>
///     {
///         options.EnableProactiveThrottling = true;
///         options.QuotaLowThreshold = 0.15;
///
///         options.OnRateLimitInfo = args =>
///         {
///             logger.LogDebug("Rate limit: {Remaining}/{Quota}",
///                 args.RateLimitInfo.Remaining,
///                 args.RateLimitInfo.Quota);
///             return ValueTask.CompletedTask;
///         };
///
///         options.OnQuotaLow = args =>
///         {
///             logger.LogWarning("Low quota warning: {Percentage:P0}",
///                 args.QuotaPercentage);
///             return ValueTask.CompletedTask;
///         };
///
///         options.OnThrottling = args =>
///         {
///             logger.LogInformation("Throttling for {Delay}ms: {Reason}",
///                 args.Delay.TotalMilliseconds,
///                 args.Reason);
///             return ValueTask.CompletedTask;
///         };
///     })
///     .Build();
/// ]]></code>
/// </example>
public sealed class RateLimitHeadersStrategyOptions : ResilienceStrategyOptions
{
    private double _quotaLowThreshold = 0.1;
    private IThrottlingAlgorithm _throttlingAlgorithm = new PercentageThrottlingAlgorithm();
    private TimeProvider _timeProvider = TimeProvider.System;

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitHeadersStrategyOptions"/> class.
    /// </summary>
    public RateLimitHeadersStrategyOptions()
    {
        Name = "RateLimitHeaders";
    }

    /// <summary>
    /// Gets or sets whether proactive throttling is enabled.
    /// When enabled, requests may be delayed based on rate limit information.
    /// Default is <c>true</c>.
    /// </summary>
    public bool EnableProactiveThrottling { get; set; } = true;

    /// <summary>
    /// Gets or sets the throttling algorithm to use.
    /// Default is <see cref="PercentageThrottlingAlgorithm"/> with default settings.
    /// </summary>
    /// <remarks>
    /// The strategy enforces a server-ordered stop (a Retry-After header, or a stored state
    /// with zero remaining requests) before consulting the algorithm; the algorithm only
    /// shapes the proactive slow-down while quota remains.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when assigned null.</exception>
    public IThrottlingAlgorithm ThrottlingAlgorithm
    {
        get => _throttlingAlgorithm;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _throttlingAlgorithm = value;
        }
    }

    /// <summary>
    /// Gets or sets the clock used for state timestamps and throttling delays.
    /// Default is the system clock (<c>TimeProvider.System</c>). Inject a fake clock in tests to
    /// control throttling waits deterministically.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when assigned null.</exception>
    public TimeProvider TimeProvider
    {
        get => _timeProvider;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _timeProvider = value;
        }
    }

    /// <summary>
    /// Gets the throttling settings: the built-in algorithm's threshold, factor, and delay cap,
    /// and the cap on the wait imposed by a server-ordered stop
    /// (<see cref="RateLimitThrottlingOptions.MaxExhaustedDelay"/>).
    /// </summary>
    public RateLimitThrottlingOptions Throttling { get; } = new();

    /// <summary>
    /// Gets or sets whether the strategy, when it cannot resolve a state key before the
    /// request (no caller-set key, no request on the context), throttles from the state of
    /// the endpoint it most recently observed a response from. Default is <c>true</c>.
    /// </summary>
    /// <remarks>
    /// The fallback has a documented miss: when executions alternate between hosts, the most
    /// recently observed endpoint can be a different host than the one this execution targets,
    /// so a stop recorded for the target host is not applied. Set a key or the request on the
    /// context (<see cref="ResilienceContextExtensions"/>) for exact per-endpoint throttling.
    /// </remarks>
    public bool ThrottleWhenStateKeyUnknown { get; set; } = true;

    /// <summary>
    /// A shared state tracker injected for testing. When null (the default), the strategy
    /// creates its own private tracker using <see cref="TimeProvider"/>.
    /// </summary>
    internal RateLimitStateTracker? StateStore { get; set; }

    /// <summary>
    /// Gets or sets whether to track rate limit state per endpoint.
    /// When enabled, state is tracked by host + path prefix combination.
    /// Default is <c>true</c>.
    /// </summary>
    public bool TrackStatePerEndpoint { get; set; } = true;

    /// <summary>
    /// Gets or sets a function to extract the state tracking key from a request.
    /// When <see cref="TrackStatePerEndpoint"/> is true, this extracts the endpoint key.
    /// Default extracts the hostname.
    /// </summary>
    /// <remarks>
    /// The default implementation uses the hostname.
    /// For example: <c>https://api.example.com/v1/users</c> becomes <c>api.example.com</c>.
    /// Set this to customize how endpoints are grouped for state tracking.
    /// </remarks>
    public Func<HttpRequestMessage, string>? StateKeyExtractor { get; set; }

    /// <summary>
    /// Gets or sets the threshold below which the quota is considered low.
    /// This is used for the <see cref="OnQuotaLow"/> callback.
    /// Value must be between 0.0 and 1.0.
    /// Default is 0.1 (10%).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is less than 0.0 or greater than 1.0.
    /// </exception>
    public double QuotaLowThreshold
    {
        get => _quotaLowThreshold;
        set
        {
            RateLimitOptionsHelper.ValidateQuotaThreshold(value, nameof(value));
            _quotaLowThreshold = value;
        }
    }

    /// <summary>
    /// Gets or sets an event that is raised when rate limit information is parsed from a response.
    /// </summary>
    public Func<OnRateLimitInfoArguments, ValueTask>? OnRateLimitInfo { get; set; }

    /// <summary>
    /// Gets or sets an event that is raised when the remaining quota falls below
    /// the <see cref="QuotaLowThreshold"/>.
    /// </summary>
    public Func<OnQuotaLowArguments, ValueTask>? OnQuotaLow { get; set; }

    /// <summary>
    /// Gets or sets an event that is raised when throttling is about to occur.
    /// </summary>
    public Func<OnThrottlingArguments, ValueTask>? OnThrottling { get; set; }

    /// <summary>
    /// Gets the state tracking key for a request using the configured extractor
    /// or the default implementation.
    /// </summary>
    /// <param name="request">The HTTP request.</param>
    /// <returns>A key identifying the endpoint for state tracking.</returns>
    internal string GetStateKey(HttpRequestMessage? request) =>
        RateLimitOptionsHelper.GetStateKey(request, TrackStatePerEndpoint, StateKeyExtractor);
}

/// <summary>
/// Arguments for the <see cref="RateLimitHeadersStrategyOptions.OnRateLimitInfo"/> event.
/// </summary>
/// <param name="Context">The resilience context.</param>
/// <param name="RateLimitInfo">The parsed rate limit information.</param>
/// <param name="Response">The HTTP response containing the rate limit headers.</param>
public readonly record struct OnRateLimitInfoArguments(
    ResilienceContext Context,
    RateLimitInfo RateLimitInfo,
    HttpResponseMessage Response);

/// <summary>
/// Arguments for the <see cref="RateLimitHeadersStrategyOptions.OnQuotaLow"/> event.
/// </summary>
/// <param name="Context">The resilience context.</param>
/// <param name="RateLimitInfo">The rate limit information with low quota.</param>
/// <param name="Response">The HTTP response containing the rate limit headers.</param>
/// <param name="QuotaPercentage">The current quota percentage (0.0 to 1.0).</param>
/// <param name="Threshold">The threshold that was exceeded.</param>
public readonly record struct OnQuotaLowArguments(
    ResilienceContext Context,
    RateLimitInfo RateLimitInfo,
    HttpResponseMessage Response,
    double QuotaPercentage,
    double Threshold);

/// <summary>
/// Arguments for the <see cref="RateLimitHeadersStrategyOptions.OnThrottling"/> event.
/// </summary>
/// <param name="Context">The resilience context.</param>
/// <param name="RateLimitInfo">The rate limit information that triggered throttling.</param>
/// <param name="Delay">The delay that will be applied.</param>
/// <param name="Reason">The reason for throttling.</param>
/// <param name="StateKey">The state tracking key the decision was based on.</param>
/// <param name="Source">Which rung of the key resolution chain produced <paramref name="StateKey"/>.</param>
public readonly record struct OnThrottlingArguments(
    ResilienceContext Context,
    RateLimitInfo RateLimitInfo,
    TimeSpan Delay,
    string? Reason,
    string? StateKey = null,
    ThrottleDecisionSource Source = ThrottleDecisionSource.None);
