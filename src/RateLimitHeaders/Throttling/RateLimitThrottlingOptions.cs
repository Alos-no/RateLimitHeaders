using RateLimitHeaders.Internal;

namespace RateLimitHeaders.Throttling;

/// <summary>
/// Throttling settings shared by the built-in algorithm and the handler's own
/// wait for a server-ordered stop (a Retry-After, or zero remaining requests).
/// </summary>
public sealed class RateLimitThrottlingOptions
{
    private double _threshold = PercentageThrottlingAlgorithm.DefaultThreshold;
    private double _factor = PercentageThrottlingAlgorithm.DefaultFactor;
    private TimeSpan _maxDelay = PercentageThrottlingAlgorithm.DefaultMaxDelay;
    private TimeSpan _maxExhaustedDelay = RateLimitDefaults.DefaultMaxExhaustedDelay;

    /// <summary>
    /// The remaining-quota share below which the built-in algorithm begins throttling
    /// (0.0 to 1.0). Default is 0.1 (10%).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is not a finite number between 0.0 and 1.0.
    /// </exception>
    public double Threshold
    {
        get => _threshold;
        set
        {
            RateLimitOptionsHelper.ValidateQuotaThreshold(value, nameof(value));
            _threshold = value;
        }
    }

    /// <summary>
    /// A multiplier applied to the built-in algorithm's computed delay. Default is 1.0.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is negative or not a finite number.
    /// </exception>
    public double Factor
    {
        get => _factor;
        set
        {
            if (!double.IsFinite(value) || value < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Factor must be a finite, non-negative number.");
            }

            _factor = value;
        }
    }

    /// <summary>
    /// The cap on the built-in algorithm's proactive delay (the slow-down applied while
    /// quota remains). Default is 5 seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is negative or exceeds what <see cref="Task.Delay(TimeSpan)"/> accepts.
    /// </exception>
    public TimeSpan MaxDelay
    {
        get => _maxDelay;
        set
        {
            RateLimitOptionsHelper.ValidateDelay(value, nameof(value));
            _maxDelay = value;
        }
    }

    /// <summary>
    /// The cap on the wait imposed when the server ordered a stop: a Retry-After header,
    /// or a stored state with zero remaining requests and the reset moment still ahead.
    /// Default is 5 minutes (<see cref="RateLimitDefaults.DefaultMaxExhaustedDelay"/>).
    /// </summary>
    /// <remarks>
    /// This cap can exceed <see cref="HttpClient.Timeout"/> (100 seconds by default);
    /// a wait longer than the ambient timeout surfaces as a <see cref="TaskCanceledException"/>
    /// from that layer. Raise the timeout or lower this cap to fit your timeout budget.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is negative or exceeds what <see cref="Task.Delay(TimeSpan)"/> accepts.
    /// </exception>
    public TimeSpan MaxExhaustedDelay
    {
        get => _maxExhaustedDelay;
        set
        {
            RateLimitOptionsHelper.ValidateDelay(value, nameof(value));
            _maxExhaustedDelay = value;
        }
    }
}
