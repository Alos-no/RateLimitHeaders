# Configuration

This guide covers all configuration options available in RateLimitHeaders.

## Handler Options

The `RateLimitHandlerOptions` class configures the DelegatingHandler:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `EnableProactiveThrottling` | `bool` | `false` | Enable automatic request delays when quota is low |
| `QuotaLowThreshold` | `double` | `0.1` | Threshold (0-1) for triggering quota low warnings |
| `ThrottlingAlgorithm` | `IThrottlingAlgorithm` | `PercentageThrottlingAlgorithm` | Algorithm for calculating throttle delays |
| `StateKeyExtractor` | `Func<HttpRequestMessage, string>` | Hostname | Function to extract state tracking key from requests |
| `OnRateLimitInfo` | `Func<RateLimitInfoEventArgs, ValueTask>?` | `null` | Callback when rate limit headers are parsed |
| `OnQuotaLow` | `Func<QuotaLowEventArgs, ValueTask>?` | `null` | Callback when quota falls below threshold |
| `OnThrottling` | `Func<ThrottlingEventArgs, ValueTask>?` | `null` | Callback before a request is throttled |

## Proactive Throttling

When `EnableProactiveThrottling` is `true`, the handler automatically delays requests when quota is low:

```csharp
options.EnableProactiveThrottling = true;
options.ThrottlingAlgorithm = new PercentageThrottlingAlgorithm(
    threshold: 0.1,    // Start throttling at 10% remaining
    factor: 1.0,       // Delay multiplier
    maxDelay: TimeSpan.FromSeconds(5));
```

### PercentageThrottlingAlgorithm

The default algorithm calculates delays as:

```
Delay = (threshold - remainingPercentage) * resetSeconds * factor
```

| Parameter | Default | Description |
|-----------|---------|-------------|
| `threshold` | `0.1` | Start throttling when remaining quota falls below this percentage |
| `factor` | `1.0` | Multiplier for calculated delay |
| `maxDelay` | `5 seconds` | Maximum delay to apply |

## State Key Extraction

By default, rate limit state is tracked per hostname:

```csharp
// Default: hostname only
// api.example.com/v1/users -> "api.example.com"
// api.example.com/v1/orders -> "api.example.com"
```

Customize state key extraction for more granular tracking:

```csharp
// Track per path segment
options.StateKeyExtractor = request =>
{
    var uri = request.RequestUri;
    if (uri is null) return "default";
    var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    return segments.Length > 0 ? $"{uri.Host}/{segments[0]}" : uri.Host;
};

// Track per API key
options.StateKeyExtractor = request =>
{
    var apiKey = request.Headers.TryGetValues("X-API-Key", out var values)
        ? values.FirstOrDefault()
        : null;
    var host = request.RequestUri?.Host ?? "default";
    return apiKey is not null ? $"{host}:{apiKey}" : host;
};
```

### Partition Key Considerations

Some APIs partition rate limits by tenant, user, or API key (indicated by the `pk` parameter in response headers). The library parses this value into `RateLimitInfo.PartitionKey`, but proactive throttling does not automatically use it because:

1. The state key must be computed *before* sending the request
2. The partition key is only known *after* receiving the response

Use `StateKeyExtractor` to include the partition identifier from your request if your API uses partition-based rate limiting.

## Event Callbacks

### OnRateLimitInfo

Called whenever rate limit headers are successfully parsed:

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

Called when remaining quota falls below `QuotaLowThreshold`:

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

Called before a request is throttled (when proactive throttling is enabled):

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

## IETF RateLimit Headers

The library parses headers according to [draft-ietf-httpapi-ratelimit-headers-10](https://datatracker.ietf.org/doc/draft-ietf-httpapi-ratelimit-headers/):

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
| `PartitionKey` | Optional partition key from `pk` parameter |
| `IsValid` | Whether at least one header was successfully parsed |

## Thread Safety

The library is designed to be thread-safe:

- `RateLimitInfo` is a readonly struct (immutable)
- State tracking uses `ConcurrentDictionary`
- Handlers are transient (as required by IHttpClientFactory)

## Polly-Specific Options

When using with Polly, the same options apply via `RateLimitHeadersOptions`:

```csharp
var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
    .AddRateLimitHeaders(options =>
    {
        options.EnableProactiveThrottling = true;
        options.QuotaLowThreshold = 0.1;
        options.OnRateLimitInfo = args => { /* ... */ };
    })
    .Build();
```

Rate limit info is stored in the `ResilienceContext` and can be accessed via:

```csharp
context.Properties.TryGetValue(RateLimitContextProperties.RateLimitInfoKey, out var info);
```
