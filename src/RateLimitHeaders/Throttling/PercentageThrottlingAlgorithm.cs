using RateLimitHeaders.Parsing;

namespace RateLimitHeaders.Throttling;

/// <summary>
/// A simple percentage-based throttling algorithm that begins throttling when
/// the remaining quota falls below a configurable threshold.
/// </summary>
/// <remarks>
/// <para>
/// This algorithm calculates delay using the formula:
/// <code>
/// if (remaining / quota &lt; threshold) {
///     delay = (threshold - remainingPct) * resetSeconds * factor
///     delay = min(delay, maxDelay)
/// }
/// </code>
/// </para>
/// <para>
/// Example with default settings (10% threshold, 1.0 factor, 5s max):
/// <list type="bullet">
/// <item>At 8% remaining with 60s reset: delay = (0.10 - 0.08) * 60 * 1.0 = 1.2s</item>
/// <item>At 5% remaining with 60s reset: delay = (0.10 - 0.05) * 60 * 1.0 = 3.0s</item>
/// <item>At 2% remaining with 60s reset: delay = (0.10 - 0.02) * 60 * 1.0 = 4.8s</item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <para>Using with default settings:</para>
/// <code><![CDATA[
/// services.AddHttpClient("MyApi")
///     .AddRateLimitAwareHandler(options =>
///     {
///         options.ThrottlingAlgorithm = new PercentageThrottlingAlgorithm();
///     });
/// ]]></code>
/// <para>Using with custom settings (more aggressive throttling):</para>
/// <code><![CDATA[
/// var algorithm = new PercentageThrottlingAlgorithm(
///     threshold: 0.2,              // Start throttling at 20% remaining
///     factor: 2.0,                 // Double the calculated delay
///     maxDelay: TimeSpan.FromSeconds(10)); // Cap at 10 seconds
///
/// services.AddHttpClient("MyApi")
///     .AddRateLimitAwareHandler(options =>
///     {
///         options.ThrottlingAlgorithm = algorithm;
///     });
/// ]]></code>
/// </example>
public sealed class PercentageThrottlingAlgorithm : IThrottlingAlgorithm
{
    private readonly double _threshold;
    private readonly double _factor;
    private readonly TimeSpan _maxDelay;

    /// <summary>
    /// The default threshold (10% of quota remaining).
    /// </summary>
    public const double DefaultThreshold = 0.1;

    /// <summary>
    /// The default delay factor.
    /// </summary>
    public const double DefaultFactor = 1.0;

    /// <summary>
    /// The default maximum delay (5 seconds).
    /// </summary>
    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Initializes a new instance with default settings.
    /// </summary>
    public PercentageThrottlingAlgorithm()
        : this(DefaultThreshold, DefaultFactor, DefaultMaxDelay)
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified settings.
    /// </summary>
    /// <param name="threshold">
    /// The quota percentage threshold below which throttling begins (0.0 to 1.0).
    /// Default is 0.1 (10%).
    /// </param>
    /// <param name="factor">
    /// A multiplier applied to the calculated delay. Higher values result in
    /// more aggressive throttling. Default is 1.0.
    /// </param>
    /// <param name="maxDelay">
    /// The maximum delay that can be applied. Prevents excessive delays when
    /// quota is nearly exhausted. Default is 5 seconds.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="threshold"/> is not between 0.0 and 1.0,
    /// <paramref name="factor"/> is negative, or <paramref name="maxDelay"/> is negative.
    /// </exception>
    public PercentageThrottlingAlgorithm(double threshold, double factor, TimeSpan maxDelay)
    {
        if (!double.IsFinite(threshold) || threshold < 0.0 || threshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Threshold must be a finite number between 0.0 and 1.0.");
        }

        if (!double.IsFinite(factor) || factor < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "Factor must be a finite, non-negative number.");
        }

        if (maxDelay < TimeSpan.Zero || maxDelay > Internal.RateLimitOptionsHelper.MaxSupportedDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDelay),
                maxDelay,
                $"Maximum delay must be between 0 and {Internal.RateLimitOptionsHelper.MaxSupportedDelay} (the largest value Task.Delay accepts).");
        }

        _threshold = threshold;
        _factor = factor;
        _maxDelay = maxDelay;
    }

    /// <summary>
    /// Gets the threshold below which throttling begins.
    /// </summary>
    public double Threshold => _threshold;

    /// <summary>
    /// Gets the delay factor multiplier.
    /// </summary>
    public double Factor => _factor;

    /// <summary>
    /// Gets the maximum delay that can be applied.
    /// </summary>
    public TimeSpan MaxDelay => _maxDelay;

    /// <inheritdoc />
    public ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo)
    {
        // Cannot throttle without valid rate limit info
        if (!rateLimitInfo.IsValid)
        {
            return ThrottlingResult.NoThrottle;
        }

        // Cannot calculate percentage without a positive quota
        if (rateLimitInfo.Quota <= 0)
        {
            return ThrottlingResult.NoThrottle;
        }

        // A quota-only entry (no server-sent remaining count) advertises a limit without
        // reporting consumption; treating its Remaining of 0 as exhaustion would throttle
        // on a phantom signal
        if (!rateLimitInfo.HasRemaining)
        {
            return ThrottlingResult.NoThrottle;
        }

        double remainingPercentage = rateLimitInfo.GetRemainingPercentage();

        // No throttling needed if above threshold
        if (remainingPercentage >= _threshold)
        {
            return ThrottlingResult.NoThrottle;
        }

        // Calculate delay: (threshold - remainingPct) * resetSeconds * factor.
        // Clamp in double BEFORE converting: TimeSpan.FromSeconds overflows on huge inputs
        // (long reset values times a large factor), so the cap must be applied first.
        double delaySeconds = (_threshold - remainingPercentage) * rateLimitInfo.ResetSeconds * _factor;
        double maxDelaySeconds = _maxDelay.TotalSeconds;
        if (double.IsNaN(delaySeconds) || delaySeconds > maxDelaySeconds)
        {
            delaySeconds = maxDelaySeconds;
        }

        TimeSpan delay = TimeSpan.FromSeconds(delaySeconds);

        // Don't throttle for negligible delays (less than 10ms)
        if (delay.TotalMilliseconds < 10)
        {
            return ThrottlingResult.NoThrottle;
        }

        string reason = $"Quota at {remainingPercentage:P1} (below {_threshold:P0} threshold)";
        return ThrottlingResult.Throttle(delay, reason);
    }
}
