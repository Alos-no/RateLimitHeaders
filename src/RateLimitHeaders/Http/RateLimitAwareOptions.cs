using RateLimitHeaders.Events;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Throttling;

namespace RateLimitHeaders.Http;

/// <summary>
/// Configuration options for the rate limit aware HTTP handler.
/// </summary>
/// <example>
/// <para>Basic configuration with callbacks:</para>
/// <code><![CDATA[
/// services.AddHttpClient("MyApi")
///     .AddRateLimitAwareHandler(options =>
///     {
///         options.EnableProactiveThrottling = true;
///         options.QuotaLowThreshold = 0.2;  // 20%
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
///             logger.LogWarning("Low quota: {Percentage:P0} remaining",
///                 args.RemainingPercentage);
///             return ValueTask.CompletedTask;
///         };
///     });
/// ]]></code>
/// <para>Configuration from appsettings.json:</para>
/// <code><![CDATA[
/// // In appsettings.json:
/// // {
/// //   "RateLimitHandler": {
/// //     "EnableProactiveThrottling": true,
/// //     "QuotaLowThreshold": 0.15
/// //   }
/// // }
///
/// services.AddHttpClient("MyApi")
///     .AddRateLimitAwareHandler(configuration.GetSection("RateLimitHandler"));
/// ]]></code>
/// </example>
public sealed class RateLimitAwareOptions
{
    private double _quotaLowThreshold = 0.1;
    private IThrottlingAlgorithm _throttlingAlgorithm = new PercentageThrottlingAlgorithm();
    private TimeProvider _timeProvider = TimeProvider.System;

    /// <summary>
    /// Gets or sets whether proactive throttling is enabled.
    /// When enabled, requests may be delayed based on rate limit information.
    /// Default is <c>true</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When throttling is triggered, the request is delayed using <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// This holds the request until the delay completes or cancellation is requested.
    /// </para>
    /// <para>
    /// For scenarios where blocking is unacceptable, consider:
    /// <list type="bullet">
    /// <item>Setting this to <c>false</c> and using the <see cref="OnThrottling"/> callback to implement custom rejection logic</item>
    /// <item>Using a custom <see cref="ThrottlingAlgorithm"/> that returns <see cref="ThrottlingResult.NoThrottle"/></item>
    /// <item>Passing a <see cref="CancellationToken"/> with a timeout to bound the maximum delay</item>
    /// </list>
    /// </para>
    /// </remarks>
    public bool EnableProactiveThrottling { get; set; } = true;

    /// <summary>
    /// Gets or sets the throttling algorithm to use.
    /// Default is <see cref="PercentageThrottlingAlgorithm"/> with default settings.
    /// </summary>
    /// <remarks>
    /// The handler enforces a server-ordered stop (a Retry-After header, or a stored state with
    /// zero remaining requests) before consulting the algorithm; the algorithm only shapes the
    /// proactive slow-down while quota remains.
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
    /// Default is <see cref="TimeProvider.System"/>. Inject a fake clock in tests to
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
    /// and the cap on the wait imposed by a server-ordered stop (<see cref="RateLimitThrottlingOptions.MaxExhaustedDelay"/>).
    /// </summary>
    public RateLimitThrottlingOptions Throttling { get; } = new();

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
    /// Gets or sets a callback invoked whenever rate limit headers are parsed from a response.
    /// This callback is invoked regardless of the rate limit state.
    /// </summary>
    public Func<RateLimitEventArgs, ValueTask>? OnRateLimitInfo { get; set; }

    /// <summary>
    /// Gets or sets a callback invoked when the remaining quota falls below
    /// the <see cref="QuotaLowThreshold"/>.
    /// </summary>
    public Func<QuotaLowEventArgs, ValueTask>? OnQuotaLow { get; set; }

    /// <summary>
    /// Gets or sets a callback invoked when a request is being throttled.
    /// This is called before the delay is applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This callback can be used for logging, metrics, or to implement custom rejection logic
    /// (e.g., by throwing an exception if you prefer to fail fast rather than delay).
    /// </para>
    /// <para>
    /// The callback is invoked synchronously before the throttling delay begins.
    /// </para>
    /// </remarks>
    public Func<ThrottlingEventArgs, ValueTask>? OnThrottling { get; set; }

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
    /// Gets the state tracking key for a request using the configured extractor
    /// or the default implementation.
    /// </summary>
    /// <param name="request">The HTTP request.</param>
    /// <returns>A key identifying the endpoint for state tracking.</returns>
    internal string GetStateKey(HttpRequestMessage request) =>
        RateLimitOptionsHelper.GetStateKey(request, TrackStatePerEndpoint, StateKeyExtractor);
}
