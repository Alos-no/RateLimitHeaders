# Getting Started

This guide will help you get up and running with RateLimitHeaders.

## Prerequisites

- .NET 8.0, .NET 9.0, or .NET 10.0 SDK

## Installation

Install the packages you need from NuGet:

### [.NET CLI](#tab/dotnet-cli)

```bash
# Core library with DelegatingHandler
dotnet add package RateLimitHeaders

# Polly v8 integration (optional)
dotnet add package RateLimitHeaders.Polly
```

### [Package Manager](#tab/package-manager)

```powershell
# Core library with DelegatingHandler
Install-Package RateLimitHeaders

# Polly v8 integration (optional)
Install-Package RateLimitHeaders.Polly
```

---

## Quick Start with IHttpClientFactory

The easiest way to use RateLimitHeaders is with IHttpClientFactory:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("MyApi", client =>
{
    client.BaseAddress = new Uri("https://api.example.com");
})
.AddRateLimitAwareHandler(options =>
{
    options.EnableProactiveThrottling = true;
    options.QuotaLowThreshold = 0.1; // 10%
});

var app = builder.Build();
```

Then inject and use the HttpClient:

```csharp
public class MyService(IHttpClientFactory httpClientFactory)
{
    public async Task<string> GetDataAsync()
    {
        var client = httpClientFactory.CreateClient("MyApi");
        var response = await client.GetAsync("/api/resource");
        return await response.Content.ReadAsStringAsync();
    }
}
```

## Quick Start with Polly

If you're using Polly for resilience, add rate limit awareness to your pipeline:

```csharp
var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
    .AddRateLimitHeaders(options =>
    {
        options.EnableProactiveThrottling = true;
    })
    .AddRetry(new RetryStrategyOptions<HttpResponseMessage>())
    .Build();
```

Use the pipeline with an HttpClient:

```csharp
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

## Accessing Rate Limit Info from Responses

### Extension Methods (Recommended)

Use the convenient extension methods on `HttpResponseMessage`:

```csharp
var response = await httpClient.GetAsync("/api/resource");

// Option 1: Direct parsing (parses headers each call)
if (response.TryGetRateLimitInfo(out var info))
{
    Console.WriteLine($"Remaining: {info.Remaining}/{info.Quota}");
}

// Option 2: Get without null check (returns invalid RateLimitInfo if no headers)
var rateLimitInfo = response.GetRateLimitInfo();
if (rateLimitInfo.IsValid)
{
    Console.WriteLine($"Policy: {rateLimitInfo.PolicyName}");
}

// Option 3: Get cached info (when using RateLimitAwareHandler)
// The handler stores parsed info in request options for efficiency
if (response.TryGetStoredRateLimitInfo(out var cachedInfo))
{
    Console.WriteLine($"Cached: {cachedInfo.Remaining}/{cachedInfo.Quota}");
}
```

### Static Parser

For more control, use the static parser directly:

```csharp
var response = await httpClient.GetAsync("/api/resource");

if (RateLimitHeaderParser.TryParse(response, out var rateLimitInfo))
{
    Console.WriteLine($"Policy: {rateLimitInfo.PolicyName}");
    Console.WriteLine($"Remaining: {rateLimitInfo.Remaining}/{rateLimitInfo.Quota}");
    Console.WriteLine($"Resets in: {rateLimitInfo.ResetSeconds}s");
    Console.WriteLine($"Window: {rateLimitInfo.WindowSeconds}s");

    if (rateLimitInfo.IsQuotaLow(0.1)) // 10% threshold
    {
        Console.WriteLine("Warning: Quota is low!");
    }
}
```

## Handler Placement Order

When using multiple delegating handlers, place the rate limit aware handler:
- **AFTER** authentication handlers (so auth headers are present for state tracking)
- **BEFORE** retry/resilience handlers (so rate limit info is available for retry decisions)

```csharp
services.AddHttpClient("MyApi")
    .AddHttpMessageHandler<AuthenticationHandler>()  // 1. Auth first
    .AddRateLimitAwareHandler()                      // 2. Rate limiting
    .AddStandardResilienceHandler();                 // 3. Retry/resilience last
```

## Event Callbacks

Monitor rate limit status with event callbacks:

```csharp
services.AddHttpClient("MyApi")
    .AddRateLimitAwareHandler(options =>
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
            logger.LogInformation("Throttling for {Delay}ms: {Reason}",
                args.Delay.TotalMilliseconds,
                args.Reason);
            return ValueTask.CompletedTask;
        };
    });
```

## Next Steps

- [Configuration](configuration.md) - All configuration options
- [Proactive Throttling](proactive-throttling.md) - How proactive throttling works
- [Polly Integration](polly-integration.md) - Using with Polly resilience pipelines
- [Custom Throttling Algorithms](custom-algorithms.md) - Implement custom throttling strategies
- [API Reference](xref:RateLimitHeaders) - Complete API documentation
