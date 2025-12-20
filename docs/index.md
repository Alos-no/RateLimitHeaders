---
_layout: landing
title: RateLimitHeaders | .NET Library for IETF RateLimit Headers
_description: "RateLimitHeaders is a .NET library for parsing IETF RateLimit headers and enabling proactive rate limit awareness in HTTP clients. Works with Polly v8 and IHttpClientFactory."
_keywords: "RateLimitHeaders, .NET rate limiting, IETF RateLimit headers, Polly, proactive throttling, HttpClient, DelegatingHandler, 429 errors"
---

<style>
/* Container to match docfx content width */
.landing-container {
  max-width: 1140px;
  margin: 0 auto;
  padding: 0 1.5rem;
}

/* Hero section */
.hero {
  text-align: center;
  padding: 3rem 0 2rem;
}

.hero h1 {
  font-size: 3rem;
  font-weight: 600;
  margin-bottom: 1rem;
  background: linear-gradient(135deg, #1aae71, #15895a);
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
  background-clip: text;
}

.hero .tagline {
  font-size: 1.25rem;
  color: var(--bs-secondary-color);
  margin-bottom: 2rem;
  max-width: 600px;
  margin-left: auto;
  margin-right: auto;
}

.hero .badges {
  margin-bottom: 2rem;
}

.hero .badges img {
  margin: 0 0.25rem;
}

/* CTA buttons */
.cta-buttons {
  display: flex;
  gap: 1rem;
  justify-content: center;
  flex-wrap: wrap;
  margin-bottom: 1rem;
}

.cta-buttons .btn {
  padding: 0.75rem 2rem;
  font-size: 1.1rem;
  font-weight: 500;
  border-radius: 0.5rem;
  text-decoration: none;
  transition: transform 0.2s, box-shadow 0.2s;
}

.cta-buttons .btn:hover {
  transform: translateY(-2px);
  box-shadow: 0 4px 12px rgba(0,0,0,0.15);
}

.cta-buttons .btn-primary {
  background: linear-gradient(135deg, #1aae71, #15895a);
  border: none;
  color: white;
}

.cta-buttons .btn-outline {
  border: 2px solid var(--bs-border-color);
  background: transparent;
  color: var(--bs-body-color);
}

/* Packages section */
.packages {
  padding: 2rem 0;
}

.packages h2 {
  text-align: center;
  margin-bottom: 2rem;
  font-weight: 600;
}

.package-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
  gap: 1.5rem;
}

.package-card {
  border: 1px solid var(--bs-border-color);
  border-radius: 0.75rem;
  padding: 1.5rem;
  background: var(--bs-body-bg);
  transition: box-shadow 0.2s, transform 0.2s;
}

.package-card:hover {
  box-shadow: 0 4px 20px rgba(0,0,0,0.1);
  transform: translateY(-2px);
}

.package-card h3 {
  font-size: 1.25rem;
  font-weight: 600;
  margin-bottom: 0.75rem;
  color: var(--bs-heading-color);
}

.package-card p {
  color: var(--bs-secondary-color);
  margin-bottom: 1rem;
  font-size: 0.95rem;
}

.package-card code {
  display: block;
  background: var(--bs-tertiary-bg);
  padding: 0.5rem 0.75rem;
  border-radius: 0.375rem;
  font-size: 0.85rem;
  margin-bottom: 1rem;
}

/* Quick start section */
.example {
  padding: 2rem;
  background: var(--bs-tertiary-bg);
  border-radius: 1rem;
  margin: 2rem 0;
}

.example h2 {
  text-align: center;
  margin-bottom: 1.5rem;
  font-weight: 600;
}

/* Features section */
.features {
  padding: 2rem 0;
}

.features h2 {
  text-align: center;
  margin-bottom: 2rem;
  font-weight: 600;
}

.feature-grid {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 1.5rem;
}

@media (max-width: 768px) {
  .feature-grid {
    grid-template-columns: 1fr;
  }
}

.feature-item {
  text-align: left;
  padding: 1.5rem;
  border: 1px solid var(--bs-border-color);
  border-radius: 0.75rem;
  background: var(--bs-body-bg);
  transition: box-shadow 0.2s, transform 0.2s;
}

.feature-item:hover {
  box-shadow: 0 4px 20px rgba(0,0,0,0.08);
  transform: translateY(-2px);
}

.feature-header {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin-bottom: 0.75rem;
}

.feature-icon {
  width: 40px;
  height: 40px;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 0.5rem;
  background: linear-gradient(135deg, rgba(26, 174, 113, 0.1), rgba(21, 137, 90, 0.1));
  flex-shrink: 0;
}

.feature-icon svg {
  width: 20px;
  height: 20px;
  color: #1aae71;
}

.feature-item h4 {
  font-weight: 600;
  margin: 0;
  font-size: 1.1rem;
}

.feature-item p {
  color: var(--bs-secondary-color);
  font-size: 0.9rem;
  margin: 0;
  line-height: 1.6;
}

/* Framework table */
.frameworks {
  padding: 2rem 0;
}

.frameworks h2 {
  text-align: center;
  margin-bottom: 1.5rem;
  font-weight: 600;
}

.frameworks table {
  margin: 0 auto;
}

/* Contributing section */
.contributing {
  padding: 2rem 0;
  text-align: center;
}

.contributing h2 {
  margin-bottom: 1rem;
  font-weight: 600;
}

.contributing p {
  color: var(--bs-secondary-color);
  margin-bottom: 1.5rem;
  max-width: 600px;
  margin-left: auto;
  margin-right: auto;
}
</style>

<div class="landing-container">

<div class="hero">
  <h1>RateLimitHeaders</h1>
  <p class="tagline">A .NET library for parsing IETF RateLimit headers and enabling proactive rate limit awareness in HTTP clients.</p>

  <div class="badges">
    <a href="https://github.com/Alos-no/RateLimitHeaders/actions/workflows/CI.yml"><img src="https://github.com/Alos-no/RateLimitHeaders/actions/workflows/CI.yml/badge.svg" alt="Build Status"></a>
    <a href="https://github.com/Alos-no/RateLimitHeaders/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="License"></a>
  </div>

  <div class="cta-buttons">
    <a href="articles/getting-started.md" class="btn btn-primary">Get Started</a>
    <a href="api/RateLimitHeaders.html" class="btn btn-outline">API Reference</a>
    <a href="https://github.com/Alos-no/RateLimitHeaders" class="btn btn-outline">GitHub</a>
  </div>
</div>

<div class="packages">
  <h2>Packages</h2>
  <div class="package-grid">
    <div class="package-card">
      <h3>RateLimitHeaders</h3>
      <p>Core library with IETF RateLimit header parsing and IHttpClientFactory DelegatingHandler for proactive throttling.</p>
      <code>dotnet add package RateLimitHeaders</code>
      <a href="https://www.nuget.org/packages/RateLimitHeaders/"><img src="https://img.shields.io/nuget/v/RateLimitHeaders?label=NuGet" alt="NuGet"></a>
    </div>
    <div class="package-card">
      <h3>RateLimitHeaders.Polly</h3>
      <p>Polly v8 integration for adding rate limit awareness to resilience pipelines.</p>
      <code>dotnet add package RateLimitHeaders.Polly</code>
      <a href="https://www.nuget.org/packages/RateLimitHeaders.Polly/"><img src="https://img.shields.io/nuget/v/RateLimitHeaders.Polly?label=NuGet" alt="NuGet"></a>
    </div>
  </div>
</div>

<div class="example">
  <h2>Quick Start</h2>

```csharp
// Using with IHttpClientFactory and Microsoft.Extensions.Http.Resilience
services.AddHttpClient("MyApi")
    .AddRateLimitAwareHandler(options =>
    {
        options.EnableProactiveThrottling = true;
        options.QuotaLowThreshold = 0.1; // Warn at 10% remaining
    })
    .AddStandardResilienceHandler(); // Microsoft resilience (retry, circuit breaker, etc.)

// Or using with Polly resilience pipelines directly
var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
    .AddRateLimitHeaders(options => options.EnableProactiveThrottling = true)
    .AddRetry(new RetryStrategyOptions<HttpResponseMessage>())
    .Build();
```

</div>

<div class="features">
  <h2>Features</h2>
  <div class="feature-grid">
    <div class="feature-item">
      <div class="feature-header">
        <div class="feature-icon">
          <svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" stroke-width="1.5" stroke="currentColor">
            <path stroke-linecap="round" stroke-linejoin="round" d="M17.25 6.75L22.5 12l-5.25 5.25m-10.5 0L1.5 12l5.25-5.25m7.5-3l-4.5 16.5" />
          </svg>
        </div>
        <h4>IETF Standard Parsing</h4>
      </div>
      <p>Parses <code>RateLimit</code> and <code>RateLimit-Policy</code> headers per draft-ietf-httpapi-ratelimit-headers-10. Extracts remaining quota, reset time, window size, and partition keys.</p>
    </div>
    <div class="feature-item">
      <div class="feature-header">
        <div class="feature-icon">
          <svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" stroke-width="1.5" stroke="currentColor">
            <path stroke-linecap="round" stroke-linejoin="round" d="M12 6v6h4.5m4.5 0a9 9 0 11-18 0 9 9 0 0118 0z" />
          </svg>
        </div>
        <h4>Proactive Throttling</h4>
      </div>
      <p>Automatically delays requests when quota is low, preventing 429 errors before they happen. Configurable thresholds, delays, and custom throttling algorithms.</p>
    </div>
    <div class="feature-item">
      <div class="feature-header">
        <div class="feature-icon">
          <svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" stroke-width="1.5" stroke="currentColor">
            <path stroke-linecap="round" stroke-linejoin="round" d="M9 12.75L11.25 15 15 9.75m-3-7.036A11.959 11.959 0 013.598 6 11.99 11.99 0 003 9.749c0 5.592 3.824 10.29 9 11.623 5.176-1.332 9-6.03 9-11.622 0-1.31-.21-2.571-.598-3.751h-.152c-3.196 0-6.1-1.248-8.25-3.285z" />
          </svg>
        </div>
        <h4>Polly Integration</h4>
      </div>
      <p>Works seamlessly with Polly v8 resilience pipelines. Add rate limit awareness alongside retry, circuit breaker, and timeout strategies.</p>
    </div>
    <div class="feature-item">
      <div class="feature-header">
        <div class="feature-icon">
          <svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" stroke-width="1.5" stroke="currentColor">
            <path stroke-linecap="round" stroke-linejoin="round" d="M13.5 16.875h3.375m0 0h3.375m-3.375 0V13.5m0 3.375v3.375M6 10.5h2.25a2.25 2.25 0 002.25-2.25V6a2.25 2.25 0 00-2.25-2.25H6A2.25 2.25 0 003.75 6v2.25A2.25 2.25 0 006 10.5zm0 9.75h2.25A2.25 2.25 0 0010.5 18v-2.25a2.25 2.25 0 00-2.25-2.25H6a2.25 2.25 0 00-2.25 2.25V18A2.25 2.25 0 006 20.25zm9.75-9.75H18a2.25 2.25 0 002.25-2.25V6A2.25 2.25 0 0018 3.75h-2.25A2.25 2.25 0 0013.5 6v2.25a2.25 2.25 0 002.25 2.25z" />
          </svg>
        </div>
        <h4>DelegatingHandler</h4>
      </div>
      <p>Drop-in handler for IHttpClientFactory. Easy integration with ASP.NET Core dependency injection and named/typed HttpClients.</p>
    </div>
    <div class="feature-item">
      <div class="feature-header">
        <div class="feature-icon">
          <svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" stroke-width="1.5" stroke="currentColor">
            <path stroke-linecap="round" stroke-linejoin="round" d="M10.5 6h9.75M10.5 6a1.5 1.5 0 11-3 0m3 0a1.5 1.5 0 10-3 0M3.75 6H7.5m3 12h9.75m-9.75 0a1.5 1.5 0 01-3 0m3 0a1.5 1.5 0 00-3 0m-3.75 0H7.5m9-6h3.75m-3.75 0a1.5 1.5 0 01-3 0m3 0a1.5 1.5 0 00-3 0m-9.75 0h9.75" />
          </svg>
        </div>
        <h4>Extensible Throttling</h4>
      </div>
      <p>Implement <code>IThrottlingAlgorithm</code> for custom strategies. Built-in percentage-based algorithm with configurable threshold, factor, and max delay.</p>
    </div>
    <div class="feature-item">
      <div class="feature-header">
        <div class="feature-icon">
          <svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" stroke-width="1.5" stroke="currentColor">
            <path stroke-linecap="round" stroke-linejoin="round" d="M14.857 17.082a23.848 23.848 0 005.454-1.31A8.967 8.967 0 0118 9.75v-.7V9A6 6 0 006 9v.75a8.967 8.967 0 01-2.312 6.022c1.733.64 3.56 1.085 5.455 1.31m5.714 0a24.255 24.255 0 01-5.714 0m5.714 0a3 3 0 11-5.714 0" />
          </svg>
        </div>
        <h4>Observable Events</h4>
      </div>
      <p>Callbacks for rate limit info, quota warnings, and throttling events. Perfect for logging, metrics, and alerting when approaching limits.</p>
    </div>
  </div>
</div>

<div class="frameworks">
  <h2>Supported Frameworks</h2>

| Package | .NET 8 | .NET 9 | .NET 10 |
|---------|:------:|:------:|:-------:|
| **RateLimitHeaders** | ✅ | ✅ | ✅ |
| **RateLimitHeaders.Polly** | ✅ | ✅ | ✅ |

</div>

<div class="contributing">
  <h2>Contributing</h2>
  <p>We welcome contributions! Whether it's bug reports, feature requests, documentation improvements, or code contributions.</p>
  <div class="cta-buttons">
    <a href="https://github.com/Alos-no/RateLimitHeaders" class="btn btn-outline">View on GitHub</a>
    <a href="https://github.com/Alos-no/RateLimitHeaders/issues" class="btn btn-outline">Report an Issue</a>
  </div>
</div>

</div>
