# RateLimitHeaders

A .NET library for parsing IETF RateLimit headers and enabling proactive rate limit awareness in HTTP clients.

[![NuGet](https://img.shields.io/nuget/v/RateLimitHeaders.svg)](https://www.nuget.org/packages/RateLimitHeaders)
[![Build Status](https://github.com/Alos-no/RateLimitHeaders/workflows/CI/badge.svg)](https://github.com/Alos-no/RateLimitHeaders/actions)

## Features

- **IETF Standard Parsing**: Parses `RateLimit` and `RateLimit-Policy` headers per [draft-ietf-httpapi-ratelimit-headers-10](https://datatracker.ietf.org/doc/draft-ietf-httpapi-ratelimit-headers/)
- **Proactive Throttling**: Delays requests before hitting 429 errors when quota is low
- **Polly Integration**: Works seamlessly with Polly v8 resilience pipelines
- **DelegatingHandler**: Drop-in handler for IHttpClientFactory
- **Extensible**: Pluggable throttling algorithms for custom strategies
- **Observable**: Callbacks for rate limit info, quota warnings, and throttling events

## Installation

```bash
# Core library (IHttpClientFactory integration)
dotnet add package RateLimitHeaders

# For Polly v8 resilience pipeline integration
dotnet add package RateLimitHeaders.Polly
```

## Quick Start

### Using with IHttpClientFactory

```csharp
services.AddHttpClient("MyApi")
    .AddRateLimitAwareHandler(options =>
    {
        options.EnableProactiveThrottling = true;
        options.QuotaLowThreshold = 0.1; // Warn at 10% remaining
        options.OnQuotaLow = args =>
        {
            logger.LogWarning("Quota low: {Remaining}/{Quota}",
                args.RateLimitInfo.Remaining,
                args.RateLimitInfo.Quota);
            return ValueTask.CompletedTask;
        };
    });
```

#### Handler Placement Order

When using multiple delegating handlers, place the rate limit aware handler:
- **AFTER** authentication handlers (so auth headers are present for state tracking)
- **BEFORE** retry/resilience handlers (so rate limit info is available for retry decisions)

```csharp
services.AddHttpClient("MyApi")
    .AddHttpMessageHandler<AuthenticationHandler>()  // 1. Auth first
    .AddRateLimitAwareHandler()                      // 2. Rate limiting
    .AddStandardResilienceHandler();                 // 3. Retry/resilience last
```

### Using with Polly Resilience Pipelines

```csharp
var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
    .AddRateLimitHeaders(options =>
    {
        options.EnableProactiveThrottling = true;
        options.OnRateLimitInfo = args =>
        {
            logger.LogDebug("Rate limit: {Info}", args.RateLimitInfo);
            return ValueTask.CompletedTask;
        };
    })
    .AddRetry(new RetryStrategyOptions<HttpResponseMessage>())
    .Build();

// Create a context to capture rate limit info
var context = ResilienceContextPool.Shared.Get(cancellationToken);

try
{
    var response = await pipeline.ExecuteAsync(
        async (ctx, ct) => await httpClient.GetAsync("/api/resource", ct),
        context,
        cancellationToken);

    // Access parsed rate limit info from context
    if (context.Properties.TryGetValue(RateLimitContextProperties.RateLimitInfoKey, out var info))
    {
        Console.WriteLine($"Remaining: {info.Remaining}/{info.Quota}");
    }
}
finally
{
    ResilienceContextPool.Shared.Return(context);
}
```

### Accessing Rate Limit Info

Use the extension methods on `HttpResponseMessage`:

```csharp
var response = await httpClient.GetAsync("/api/resource");

// Parse headers directly
if (response.TryGetRateLimitInfo(out var info))
{
    Console.WriteLine($"Remaining: {info.Remaining}/{info.Quota}");
    Console.WriteLine($"Resets in: {info.ResetSeconds}s");

    if (info.IsQuotaLow(0.1))
        Console.WriteLine("Warning: Quota is low!");
}

// Or get cached info when using RateLimitAwareHandler
if (response.TryGetStoredRateLimitInfo(out var cached))
{
    Console.WriteLine($"Cached: {cached.Remaining}/{cached.Quota}");
}
```

## IETF RateLimit Headers

This library parses the IETF standard format:

```
RateLimit: "default";r=50;t=30
RateLimit-Policy: "default";q=100;w=60
```

| Header | Format | Description |
|--------|--------|-------------|
| `RateLimit` | `"policy";r=remaining;t=reset` | Current rate limit state |
| `RateLimit-Policy` | `"policy";q=quota;w=window` | Rate limit policy definition |

### Parsed Values

| Property | Description |
|----------|-------------|
| `PolicyName` | The rate limit policy name (e.g., "default", "api-v2") |
| `Remaining` | Requests remaining in the current window |
| `ResetSeconds` | Seconds until the window resets |
| `Quota` | Maximum requests allowed per window |
| `WindowSeconds` | Duration of the rate limit window |
| `IsValid` | Whether at least one header was successfully parsed |

## Proactive Throttling

When enabled, the handler automatically delays requests when quota is low:

```csharp
// Default algorithm: PercentageThrottlingAlgorithm
// Starts throttling when remaining < 10% of quota
// Delay = (threshold - remainingPct) * resetSeconds * factor
// Max delay = 5 seconds

options.ThrottlingAlgorithm = new PercentageThrottlingAlgorithm(
    threshold: 0.1,    // Start at 10% remaining
    factor: 1.0,       // Delay multiplier
    maxDelay: TimeSpan.FromSeconds(5));
```

### Custom Throttling Algorithm

Implement `IThrottlingAlgorithm` for custom strategies:

```csharp
public class CustomThrottlingAlgorithm : IThrottlingAlgorithm
{
    public ThrottlingResult Evaluate(RateLimitInfo info)
    {
        if (!info.IsValid || info.Remaining > 5)
            return ThrottlingResult.NoThrottle;

        var delay = TimeSpan.FromSeconds(info.ResetSeconds / 2.0);
        return ThrottlingResult.Throttle(delay, "Custom throttle");
    }
}
```

## Event Callbacks

### OnRateLimitInfo

Called whenever rate limit headers are parsed:

```csharp
options.OnRateLimitInfo = args =>
{
    metrics.RecordRateLimit(
        args.RateLimitInfo.PolicyName,
        args.RateLimitInfo.Remaining,
        args.RateLimitInfo.Quota);
    return ValueTask.CompletedTask;
};
```

### OnQuotaLow

Called when remaining quota falls below threshold:

```csharp
options.OnQuotaLow = args =>
{
    alertService.SendAlert(
        $"API quota at {args.RemainingPercentage:P0}",
        args.RequestUri);
    return ValueTask.CompletedTask;
};
```

### OnThrottling

Called before a request is throttled:

```csharp
options.OnThrottling = args =>
{
    logger.LogInformation(
        "Throttling request to {Uri} for {Delay}ms: {Reason}",
        args.RequestUri,
        args.Delay.TotalMilliseconds,
        args.Reason);
    return ValueTask.CompletedTask;
};
```

## Per-Endpoint State Tracking

By default, rate limit state is tracked per hostname:

```csharp
// Default: hostname
// api.example.com/v1/users -> "api.example.com"
// api.example.com/v1/orders -> "api.example.com"

// Custom key extraction (e.g., include path segment):
options.StateKeyExtractor = request =>
{
    var uri = request.RequestUri;
    if (uri is null) return "default";
    var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    return segments.Length > 0 ? $"{uri.Host}/{segments[0]}" : uri.Host;
};
```

### Partition Key Considerations

Some APIs partition rate limits by tenant, user, or API key (indicated by the `pk` parameter in response headers). The library parses this value into `RateLimitInfo.PartitionKey`, but **proactive throttling does not automatically use it** because:

1. The state key must be computed *before* sending the request (for proactive throttling)
2. The partition key is only known *after* receiving the response

If your API uses partition-based rate limiting, use `StateKeyExtractor` to include the partition identifier from your request:

```csharp
// For APIs that partition by API key header:
options.StateKeyExtractor = request =>
{
    var apiKey = request.Headers.TryGetValues("X-API-Key", out var values)
        ? values.FirstOrDefault()
        : null;
    var host = request.RequestUri?.Host ?? "default";
    return apiKey is not null ? $"{host}:{apiKey}" : host;
};

// For APIs that partition by tenant ID in the path:
options.StateKeyExtractor = request =>
{
    var uri = request.RequestUri;
    if (uri is null) return "default";
    // Extract tenant from path like /api/tenants/{tenantId}/...
    var match = Regex.Match(uri.AbsolutePath, @"/tenants/([^/]+)");
    return match.Success ? $"{uri.Host}:{match.Groups[1].Value}" : uri.Host;
};
```

This ensures each partition is tracked independently for accurate proactive throttling.

## Compatibility

> **Note:** This library requires **.NET 8.0 or later**. Earlier framework versions are not supported.

| Framework | Supported |
|-----------|-----------|
| .NET 8.0 | Yes |
| .NET 9.0 | Yes |
| .NET 10.0 | Yes |

## Dependencies

**RateLimitHeaders** (core):
- [Microsoft.Extensions.Http](https://www.nuget.org/packages/Microsoft.Extensions.Http) (8.0.0+)
- [Microsoft.Extensions.Logging.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions) (8.0.0+)
- [Microsoft.Extensions.Options](https://www.nuget.org/packages/Microsoft.Extensions.Options) (8.0.0+)

**RateLimitHeaders.Polly** (additional):
- [Polly.Core](https://www.nuget.org/packages/Polly.Core) (8.0.0+)

## Thread Safety

The library is designed to be thread-safe:

- `RateLimitInfo` is a readonly struct (immutable)
- State tracking uses `ConcurrentDictionary`
- Handlers are transient (as required by IHttpClientFactory)

## License

MIT License - see [LICENSE](LICENSE) for details.

## Contributing

Contributions are welcome! Please read our contributing guidelines and submit PRs to the `develop` branch.

## Related Projects

- [Polly](https://github.com/App-vNext/Polly) - The .NET resilience library
- [Microsoft.Extensions.Http.Resilience](https://www.nuget.org/packages/Microsoft.Extensions.Http.Resilience) - Official Polly integration for HttpClient
