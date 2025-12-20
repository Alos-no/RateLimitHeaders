# Custom Throttling Algorithms

RateLimitHeaders allows you to implement custom throttling algorithms for specialized rate limiting strategies.

## The IThrottlingAlgorithm Interface

```csharp
public interface IThrottlingAlgorithm
{
    // Simple evaluation based on current endpoint's rate limit info
    ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo);

    // Advanced evaluation with access to all tracked endpoints (has default implementation)
    ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo, IRateLimitStateProvider? stateProvider)
        => Evaluate(rateLimitInfo);
}

public interface IRateLimitStateProvider
{
    RateLimitInfo GetRateLimitInfo(string endpointKey);
    IEnumerable<string> TrackedEndpoints { get; }
    IEnumerable<RateLimitInfo> GetAllStates();
}

public readonly struct ThrottlingResult
{
    public bool ShouldThrottle { get; }
    public TimeSpan Delay { get; }
    public string? Reason { get; }

    public static ThrottlingResult NoThrottle { get; }
    public static ThrottlingResult Throttle(TimeSpan delay, string? reason = null);
}
```

Most custom algorithms only need to implement the simple `Evaluate(RateLimitInfo)` method. The overload with `IRateLimitStateProvider` is for advanced scenarios where you need cross-endpoint visibility (e.g., global throttling across multiple API endpoints).

## Implementing a Custom Algorithm

### Fixed Threshold Algorithm

Throttle when remaining requests fall below a fixed number:

```csharp
public class FixedThresholdAlgorithm : IThrottlingAlgorithm
{
    private readonly int _minRemaining;
    private readonly TimeSpan _delay;

    public FixedThresholdAlgorithm(int minRemaining = 5, TimeSpan? delay = null)
    {
        _minRemaining = minRemaining;
        _delay = delay ?? TimeSpan.FromSeconds(1);
    }

    public ThrottlingResult Evaluate(RateLimitInfo info)
    {
        if (!info.IsValid || info.Remaining > _minRemaining)
            return ThrottlingResult.NoThrottle;

        return ThrottlingResult.Throttle(
            _delay,
            $"Only {info.Remaining} requests remaining");
    }
}
```

### Adaptive Delay Algorithm

Scale delay based on how close you are to exhausting quota:

```csharp
public class AdaptiveDelayAlgorithm : IThrottlingAlgorithm
{
    private readonly double _threshold;
    private readonly TimeSpan _minDelay;
    private readonly TimeSpan _maxDelay;

    public AdaptiveDelayAlgorithm(
        double threshold = 0.2,
        TimeSpan? minDelay = null,
        TimeSpan? maxDelay = null)
    {
        _threshold = threshold;
        _minDelay = minDelay ?? TimeSpan.FromMilliseconds(100);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(10);
    }

    public ThrottlingResult Evaluate(RateLimitInfo info)
    {
        if (!info.IsValid || info.Quota <= 0)
            return ThrottlingResult.NoThrottle;

        var remainingPct = (double)info.Remaining / info.Quota;
        if (remainingPct > _threshold)
            return ThrottlingResult.NoThrottle;

        // Scale delay: lower remaining = longer delay
        var severity = 1.0 - (remainingPct / _threshold);
        var delayRange = _maxDelay - _minDelay;
        var delay = _minDelay + TimeSpan.FromTicks((long)(delayRange.Ticks * severity));

        return ThrottlingResult.Throttle(
            delay,
            $"Quota at {remainingPct:P0}, delaying {delay.TotalMilliseconds:F0}ms");
    }
}
```

### Token Bucket Algorithm

Spread remaining requests evenly across the time window:

```csharp
public class TokenBucketAlgorithm : IThrottlingAlgorithm
{
    private readonly int _reserveTokens;

    public TokenBucketAlgorithm(int reserveTokens = 2)
    {
        _reserveTokens = reserveTokens;
    }

    public ThrottlingResult Evaluate(RateLimitInfo info)
    {
        if (!info.IsValid || info.Remaining <= _reserveTokens)
        {
            if (info.ResetSeconds > 0)
            {
                return ThrottlingResult.Throttle(
                    TimeSpan.FromSeconds(info.ResetSeconds),
                    $"Quota exhausted, waiting for reset");
            }
            return ThrottlingResult.NoThrottle;
        }

        // Calculate ideal spacing between requests
        var availableTokens = info.Remaining - _reserveTokens;
        if (availableTokens <= 0 || info.ResetSeconds <= 0)
            return ThrottlingResult.NoThrottle;

        var spacing = TimeSpan.FromSeconds(info.ResetSeconds / (double)availableTokens);

        // Only throttle if spacing is significant
        if (spacing.TotalMilliseconds < 50)
            return ThrottlingResult.NoThrottle;

        return ThrottlingResult.Throttle(
            spacing,
            $"Pacing requests: {availableTokens} requests over {info.ResetSeconds}s");
    }
}
```

## Using Custom Algorithms

### With IHttpClientFactory

```csharp
services.AddHttpClient("MyApi")
    .AddRateLimitAwareHandler(options =>
    {
        options.EnableProactiveThrottling = true;
        options.ThrottlingAlgorithm = new AdaptiveDelayAlgorithm(
            threshold: 0.3,
            maxDelay: TimeSpan.FromSeconds(5));
    });
```

### With Polly

```csharp
var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
    .AddRateLimitHeaders(options =>
    {
        options.EnableProactiveThrottling = true;
        options.ThrottlingAlgorithm = new TokenBucketAlgorithm(reserveTokens: 5);
    })
    .Build();
```

## Combining Algorithms

Create a composite algorithm that uses different strategies:

```csharp
public class CompositeThrottlingAlgorithm : IThrottlingAlgorithm
{
    private readonly IThrottlingAlgorithm _primary;
    private readonly IThrottlingAlgorithm _fallback;

    public CompositeThrottlingAlgorithm(
        IThrottlingAlgorithm primary,
        IThrottlingAlgorithm fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public ThrottlingResult Evaluate(RateLimitInfo info)
    {
        var result = _primary.Evaluate(info);
        if (result.ShouldThrottle)
            return result;

        return _fallback.Evaluate(info);
    }
}
```

## Global Throttling with IRateLimitStateProvider

For scenarios where you need to make throttling decisions based on multiple endpoints (e.g., shared API quotas), override the `Evaluate` method that receives `IRateLimitStateProvider`:

```csharp
public class GlobalThrottlingAlgorithm : IThrottlingAlgorithm
{
    private readonly double _globalThreshold;

    public GlobalThrottlingAlgorithm(double globalThreshold = 0.15)
    {
        _globalThreshold = globalThreshold;
    }

    public ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo)
    {
        // Fallback when no state provider is available
        return EvaluateSingle(rateLimitInfo);
    }

    public ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo, IRateLimitStateProvider? stateProvider)
    {
        if (stateProvider == null)
            return EvaluateSingle(rateLimitInfo);

        // Consider the lowest quota across all tracked endpoints
        var allStates = stateProvider.GetAllStates().Where(s => s.IsValid).ToList();
        if (allStates.Count == 0)
            return ThrottlingResult.NoThrottle;

        var lowestRemaining = allStates.Min(s => s.GetRemainingPercentage());
        if (lowestRemaining > _globalThreshold)
            return ThrottlingResult.NoThrottle;

        var maxReset = allStates.Max(s => s.ResetSeconds);
        var delay = TimeSpan.FromSeconds(maxReset * (1 - lowestRemaining / _globalThreshold));

        return ThrottlingResult.Throttle(
            delay,
            $"Global quota at {lowestRemaining:P0} across {allStates.Count} endpoints");
    }

    private ThrottlingResult EvaluateSingle(RateLimitInfo info)
    {
        if (!info.IsValid || info.GetRemainingPercentage() > _globalThreshold)
            return ThrottlingResult.NoThrottle;

        return ThrottlingResult.Throttle(
            TimeSpan.FromSeconds(info.ResetSeconds * 0.5),
            $"Single endpoint at {info.GetRemainingPercentage():P0}");
    }
}
```

## Algorithm Selection Guidelines

| Scenario | Recommended Algorithm |
|----------|----------------------|
| General purpose | `PercentageThrottlingAlgorithm` (default) |
| Bursty traffic with quiet periods | `FixedThresholdAlgorithm` |
| Steady request stream | `TokenBucketAlgorithm` |
| Unpredictable load patterns | `AdaptiveDelayAlgorithm` |
| Mission-critical APIs | Composite with conservative fallback |
| Shared quotas across endpoints | Custom with `IRateLimitStateProvider` |

## Testing Custom Algorithms

```csharp
[Fact]
public void AdaptiveDelay_WhenQuotaLow_ReturnsScaledDelay()
{
    var algorithm = new AdaptiveDelayAlgorithm(threshold: 0.2);

    var info = new RateLimitInfo
    {
        IsValid = true,
        Remaining = 5,
        Quota = 100,
        ResetSeconds = 60
    };

    var result = algorithm.Evaluate(info);

    Assert.True(result.ShouldThrottle);
    Assert.True(result.Delay > TimeSpan.Zero);
}
```
