# RateLimitHeaders

[![Build Status](https://github.com/Alos-no/RateLimitHeaders/workflows/CI/badge.svg)](https://github.com/Alos-no/RateLimitHeaders/actions)
[![Documentation](https://img.shields.io/badge/docs-alos.no%2Fratelimitheaders-27ae60)](https://alos.no/ratelimitheaders)
[![License: MIT](https://img.shields.io/badge/license-MIT-27ae60)](https://github.com/Alos-no/RateLimitHeaders/blob/main/LICENSE)

[![NuGet (RateLimitHeaders)](https://img.shields.io/nuget/v/RateLimitHeaders?label=RateLimitHeaders&color=27ae60)](https://www.nuget.org/packages/RateLimitHeaders/)
[![NuGet (RateLimitHeaders.Polly)](https://img.shields.io/nuget/v/RateLimitHeaders.Polly?label=RateLimitHeaders.Polly&color=27ae60)](https://www.nuget.org/packages/RateLimitHeaders.Polly/)

**RateLimitHeaders** is a .NET library for parsing [IETF RateLimit headers](https://datatracker.ietf.org/doc/draft-ietf-httpapi-ratelimit-headers/) and enabling proactive rate limit awareness in HTTP clients. It automatically delays requests before hitting 429 errors, integrates with Polly v8 resilience pipelines, and provides a drop-in DelegatingHandler for IHttpClientFactory.

**[Documentation](https://alos.no/ratelimitheaders)** | **[Getting Started](https://alos.no/ratelimitheaders/articles/getting-started.html)** | **[API Reference](https://alos.no/ratelimitheaders/api/)**

## Packages

| Package | Description |
|---------|-------------|
| **RateLimitHeaders** | Core library with IETF header parsing and IHttpClientFactory DelegatingHandler |
| **RateLimitHeaders.Polly** | Polly v8 resilience pipeline integration |

```bash
dotnet add package RateLimitHeaders
dotnet add package RateLimitHeaders.Polly  # Optional: for Polly integration
```

## Example

```csharp
// Register with IHttpClientFactory
services.AddHttpClient("MyApi")
    .AddRateLimitAwareHandler(options =>
    {
        options.EnableProactiveThrottling = true;
        options.QuotaLowThreshold = 0.1; // Warn at 10% remaining
    })
    .AddStandardResilienceHandler();

// Or use with Polly resilience pipelines
var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
    .AddRateLimitHeaders(options => options.EnableProactiveThrottling = true)
    .AddRetry(new RetryStrategyOptions<HttpResponseMessage>())
    .Build();
```

## Features

- **IETF Standard Parsing** - Parses `RateLimit` and `RateLimit-Policy` headers per draft-ietf-httpapi-ratelimit-headers-10

- **Proactive Throttling** - Automatically delays requests when quota is low, preventing 429 errors before they happen

- **Polly Integration** - Works seamlessly with Polly v8 resilience pipelines alongside retry and circuit breaker strategies

- **DelegatingHandler** - Drop-in handler for IHttpClientFactory with full dependency injection support

- **Extensible** - Implement `IThrottlingAlgorithm` for custom throttling strategies

- **Observable** - Callbacks for rate limit info, quota warnings, and throttling events

## Get Started

<div align="center">

### 📚 Ready to dive in?

**[Explore the Full Documentation →](https://alos.no/ratelimitheaders)**

*Comprehensive guides, configuration options, custom algorithms, and more.*

</div>

## Supported Frameworks

| Package | .NET 8 | .NET 9 | .NET 10 |
|---------|:------:|:------:|:-------:|
| **RateLimitHeaders** | Yes | Yes | Yes |
| **RateLimitHeaders.Polly** | Yes | Yes | Yes |

## Contributing

We welcome contributions! Whether it's bug reports, feature requests, or code contributions.

- [Report an Issue](https://github.com/Alos-no/RateLimitHeaders/issues)
- [View Documentation](https://alos.no/ratelimitheaders)

## License

This project is licensed under the [MIT License](LICENSE).

## Related Projects

- [Polly](https://github.com/App-vNext/Polly) - The .NET resilience library
- [Microsoft.Extensions.Http.Resilience](https://www.nuget.org/packages/Microsoft.Extensions.Http.Resilience) - Official Polly integration for HttpClient
