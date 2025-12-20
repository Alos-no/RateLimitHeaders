# Polly Integration

RateLimitHeaders.Polly provides seamless integration with Polly v8 resilience pipelines, allowing you to add rate limit awareness alongside retry, circuit breaker, and timeout strategies.

## Installation

```bash
dotnet add package RateLimitHeaders.Polly
```

## Basic Usage

Add rate limit awareness to your resilience pipeline:

```csharp
using Polly;
using RateLimitHeaders.Polly;

var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
    .AddRateLimitHeaders(options =>
    {
        options.EnableProactiveThrottling = true;
    })
    .AddRetry(new RetryStrategyOptions<HttpResponseMessage>())
    .Build();
```

## Pipeline Order

The order of strategies in your pipeline matters. For rate limit awareness:

```csharp
var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
    // 1. Rate limit headers - processes responses and may delay requests
    .AddRateLimitHeaders(options => options.EnableProactiveThrottling = true)

    // 2. Retry - retries failed requests (including 429s)
    .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
    {
        ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
            .HandleResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .HandleResult(r => !r.IsSuccessStatusCode),
        MaxRetryAttempts = 3,
        Delay = TimeSpan.FromSeconds(1)
    })

    // 3. Timeout - per-attempt timeout
    .AddTimeout(TimeSpan.FromSeconds(30))
    .Build();
```

## Accessing Rate Limit Info

Rate limit information is stored in the `ResilienceContext` after each request:

```csharp
var context = ResilienceContextPool.Shared.Get(cancellationToken);

try
{
    var response = await pipeline.ExecuteAsync(
        async (ctx, ct) => await httpClient.GetAsync("/api/resource", ct),
        context,
        cancellationToken);

    // Access parsed rate limit info
    if (context.Properties.TryGetValue(
        RateLimitContextProperties.RateLimitInfoKey,
        out var info))
    {
        Console.WriteLine($"Policy: {info.PolicyName}");
        Console.WriteLine($"Remaining: {info.Remaining}/{info.Quota}");
        Console.WriteLine($"Resets in: {info.ResetSeconds}s");
    }
}
finally
{
    ResilienceContextPool.Shared.Return(context);
}
```

## Event Callbacks

Monitor rate limit events within your pipeline:

```csharp
var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
    .AddRateLimitHeaders(options =>
    {
        options.EnableProactiveThrottling = true;

        options.OnRateLimitInfo = args =>
        {
            logger.LogDebug("Rate limit: {Remaining}/{Quota}",
                args.RateLimitInfo.Remaining,
                args.RateLimitInfo.Quota);
            return ValueTask.CompletedTask;
        };

        options.OnQuotaLow = args =>
        {
            logger.LogWarning("Quota low: {Percentage:P0}",
                args.RemainingPercentage);
            return ValueTask.CompletedTask;
        };

        options.OnThrottling = args =>
        {
            logger.LogInformation("Throttling for {Delay}ms",
                args.Delay.TotalMilliseconds);
            return ValueTask.CompletedTask;
        };
    })
    .Build();
```

## Combining with HttpClient Resilience

If you're using `Microsoft.Extensions.Http.Resilience`, you can add rate limit awareness to the standard resilience handler:

```csharp
services.AddHttpClient("MyApi")
    .AddResilienceHandler("rate-limit-aware", builder =>
    {
        builder.AddRateLimitHeaders(options =>
        {
            options.EnableProactiveThrottling = true;
        });
    })
    .AddStandardResilienceHandler();
```

## Using with Typed Clients

```csharp
public interface IMyApiClient
{
    Task<string> GetResourceAsync(CancellationToken ct = default);
}

public class MyApiClient : IMyApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public MyApiClient(HttpClient httpClient, ResiliencePipeline<HttpResponseMessage> pipeline)
    {
        _httpClient = httpClient;
        _pipeline = pipeline;
    }

    public async Task<string> GetResourceAsync(CancellationToken ct = default)
    {
        var response = await _pipeline.ExecuteAsync(
            async (_, token) => await _httpClient.GetAsync("/api/resource", token),
            ResilienceContextPool.Shared.Get(ct),
            ct);

        return await response.Content.ReadAsStringAsync(ct);
    }
}
```

## Best Practices

1. **Place rate limiting first**: Add `AddRateLimitHeaders()` before retry strategies so throttling happens before each attempt
2. **Use context pooling**: Always use `ResilienceContextPool` for efficient context management
3. **Return contexts**: Always return borrowed contexts in a `finally` block
4. **Combine strategies**: Use with retry and circuit breaker for comprehensive resilience
5. **Monitor events**: Use callbacks for observability into rate limit behavior
