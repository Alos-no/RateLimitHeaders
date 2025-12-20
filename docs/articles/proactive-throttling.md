# Proactive Throttling

Proactive throttling automatically delays requests when your API quota is running low, helping you avoid 429 (Too Many Requests) errors before they happen.

## How It Works

When proactive throttling is enabled, the library:

1. Parses `RateLimit` and `RateLimit-Policy` headers from each response
2. Tracks the remaining quota for each API endpoint
3. Before sending a request, checks if remaining quota is below the threshold
4. If quota is low, delays the request to spread usage across the remaining time window

## Enabling Proactive Throttling

### With IHttpClientFactory

```csharp
services.AddHttpClient("MyApi")
    .AddRateLimitAwareHandler(options =>
    {
        options.EnableProactiveThrottling = true;
    });
```

### With Polly

```csharp
var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
    .AddRateLimitHeaders(options =>
    {
        options.EnableProactiveThrottling = true;
    })
    .Build();
```

## The Default Algorithm

The built-in `PercentageThrottlingAlgorithm` calculates delays based on:

- **Threshold**: When to start throttling (default: 10% remaining)
- **Factor**: Delay multiplier (default: 1.0)
- **Max Delay**: Maximum delay cap (default: 5 seconds)

The delay formula is:

```
delay = (threshold - remainingPercentage) * resetSeconds * factor
delay = min(delay, maxDelay)
```

### Example Scenarios

| Remaining | Quota | Reset | Delay |
|-----------|-------|-------|-------|
| 15/100 (15%) | 100 | 30s | No delay (above 10% threshold) |
| 8/100 (8%) | 100 | 30s | 0.6s ((0.10 - 0.08) × 30 × 1.0) |
| 3/100 (3%) | 100 | 30s | 2.1s ((0.10 - 0.03) × 30 × 1.0) |
| 1/100 (1%) | 100 | 60s | 5.0s (capped at maxDelay) |

## Customizing the Algorithm

```csharp
options.ThrottlingAlgorithm = new PercentageThrottlingAlgorithm(
    threshold: 0.2,    // Start at 20% remaining
    factor: 0.5,       // Half the calculated delay
    maxDelay: TimeSpan.FromSeconds(10));
```

## Monitoring Throttling

Use the `OnThrottling` callback to log or monitor when requests are delayed:

```csharp
options.OnThrottling = args =>
{
    logger.LogInformation(
        "Throttling request to {Uri} for {Delay:F1}ms. Reason: {Reason}",
        args.RequestUri,
        args.Delay.TotalMilliseconds,
        args.Reason);
    return ValueTask.CompletedTask;
};
```

## Advanced Scenarios

### Fail Fast Instead of Delaying

Some applications prefer to reject requests immediately rather than block. Use the `OnThrottling` callback to throw an exception instead of waiting:

```csharp
options.EnableProactiveThrottling = true;
options.OnThrottling = args =>
{
    // Fail fast instead of delaying
    throw new RateLimitExceededException(
        $"Rate limit exceeded for {args.RequestUri}. " +
        $"Would have delayed {args.Delay.TotalSeconds:F1}s. " +
        $"Remaining: {args.RateLimitInfo.Remaining}/{args.RateLimitInfo.Quota}");
};
```

This pattern is useful when:
- Your application has strict latency requirements
- You want to surface rate limiting to callers rather than hide it
- You prefer to handle rate limiting at a higher level (e.g., circuit breaker, queue)

### Bounding Maximum Delay with CancellationToken

Set a timeout so throttling delays don't exceed your application's tolerance:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var response = await client.GetAsync("https://api.example.com/resource", cts.Token);
```

The throttling delay will be cancelled if it exceeds the timeout, throwing an `OperationCanceledException`.

### Manual Handling Without Automatic Delays

Disable automatic throttling but still get notified about rate limit status for custom handling:

```csharp
options.EnableProactiveThrottling = false;
options.OnRateLimitInfo = args =>
{
    var remaining = args.RateLimitInfo.GetRemainingPercentage();
    if (remaining < 0.1)
    {
        logger.LogWarning("Rate limit critically low: {Percent:P0}", remaining);
        // Custom logic: queue request, circuit break, etc.
    }
    return ValueTask.CompletedTask;
};
```

This approach gives you full control to implement custom strategies like:
- Request queuing with priority
- Circuit breaking when quota is exhausted
- Alerting and metrics without affecting request flow

## Best Practices

1. **Start conservative**: Begin with default settings and adjust based on your API's behavior
2. **Monitor quota usage**: Use `OnRateLimitInfo` to track quota consumption patterns
3. **Set appropriate thresholds**: APIs with bursty traffic may need higher thresholds
4. **Consider max delay**: Long delays may cause timeout issues in your application
5. **Use with retry policies**: Combine with Polly retry for handling 429s that still occur

## When Not to Use

Proactive throttling may not be appropriate when:

- Your API doesn't return IETF RateLimit headers
- You need immediate request execution regardless of quota
- Rate limits are partitioned in ways you can't predict from the request

In these cases, consider using reactive rate limiting with retry policies instead.
