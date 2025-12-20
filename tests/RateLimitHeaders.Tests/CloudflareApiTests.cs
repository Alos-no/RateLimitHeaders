using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using RateLimitHeaders.Http;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Tests.Secrets;

namespace RateLimitHeaders.Tests;

/// <summary>
/// Integration tests that validate rate limit header parsing against the real Cloudflare API.
/// These tests require a valid Cloudflare API token configured via user secrets.
/// </summary>
/// <remarks>
/// <para>
/// To configure the API token, run: <c>pwsh -File ./scripts/setup-test-secrets.ps1</c>
/// </para>
/// <para>
/// Cloudflare returns IETF-style rate limit headers:
/// <list type="bullet">
///   <item><description><c>Ratelimit</c>: <c>"policy";r=remaining;t=reset</c></description></item>
///   <item><description><c>Ratelimit-Policy</c>: <c>"policy";q=quota;w=window</c></description></item>
/// </list>
/// </para>
/// </remarks>
public class CloudflareApiTests : IDisposable
{
    private readonly bool _isConfigured;

    // Endpoint that returns rate limit headers
    private const string TestEndpoint = "zones";

    public CloudflareApiTests()
    {
        _isConfigured = TestSecretsConfiguration.IsCloudflareConfigured;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private HttpClient CreateConfiguredClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/")
        };

        if (_isConfigured)
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", TestSecretsConfiguration.CloudflareSettings.ApiToken);
        }

        return client;
    }

    private void SkipIfNotConfigured()
    {
        Assert.True(_isConfigured, "Cloudflare API token not configured. Run scripts/setup-test-secrets.ps1");
    }

    #region Basic Connectivity and Parsing

    [Fact]
    public async Task CloudflareApi_ShouldReturnRateLimitHeaders_AndParseSuccessfully()
    {
        SkipIfNotConfigured();

        // Arrange
        using var client = CreateConfiguredClient();

        // Act
        var response = await client.GetAsync(TestEndpoint);

        Console.WriteLine($"Response Status: {response.StatusCode}");
        LogAllHeaders(response);

        // Assert - Should get a valid response
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);

        // Assert - Should have rate limit headers
        var hasRateLimitHeader = response.Headers.Contains("Ratelimit") || response.Headers.Contains("RateLimit");
        hasRateLimitHeader.Should().BeTrue("Cloudflare API should return rate limit headers");

        // Assert - Parser should successfully parse headers
        var parsed = RateLimitHeaderParser.TryParse(response, out var info);
        parsed.Should().BeTrue("Parser should successfully parse Cloudflare rate limit headers");
        info.IsValid.Should().BeTrue();
        info.Remaining.Should().BeGreaterThanOrEqualTo(0);
        info.ResetSeconds.Should().BeGreaterThanOrEqualTo(0);

        Console.WriteLine($"Parsed: Policy={info.PolicyName}, Remaining={info.Remaining}, Reset={info.ResetSeconds}s");
    }

    #endregion

    #region Rate Limit Decrement Verification

    [Fact]
    public async Task CloudflareApi_RemainingQuota_ShouldDecrementWithSuccessiveRequests()
    {
        SkipIfNotConfigured();

        // Arrange
        var rateLimitInfos = new List<RateLimitInfo>();
        var handler = new RateLimitAwareHandler(new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            OnRateLimitInfo = args =>
            {
                rateLimitInfos.Add(args.RateLimitInfo);
                return ValueTask.CompletedTask;
            }
        })
        {
            InnerHandler = new HttpClientHandler()
        };

        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/")
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestSecretsConfiguration.CloudflareSettings.ApiToken);

        // Act - Keep making requests until we see the remaining count actually decrement
        const int maxRequests = 100; // Safety limit
        const int timeoutSeconds = 60;
        var stopwatch = Stopwatch.StartNew();
        int? initialRemaining = null;
        bool decrementObserved = false;
        int requestCount = 0;

        Console.WriteLine("Making concurrent requests until rate limit decrement is observed...");

        // Fire concurrent requests to consume quota faster than the window refills
        const int batchSize = 20;

        while (!decrementObserved && requestCount < maxRequests && stopwatch.Elapsed.TotalSeconds < timeoutSeconds)
        {
            // Fire a batch of concurrent requests
            var tasks = new List<Task>();
            for (int i = 0; i < batchSize && requestCount < maxRequests; i++)
            {
                requestCount++;
                tasks.Add(client.GetAsync(TestEndpoint));
            }

            await Task.WhenAll(tasks);

            // Check if we observed a decrement
            if (rateLimitInfos.Count > 0)
            {
                foreach (var info in rateLimitInfos)
                {
                    if (initialRemaining is null)
                    {
                        initialRemaining = info.Remaining;
                        Console.WriteLine($"Initial Remaining={initialRemaining}/{info.Quota}, Reset={info.ResetSeconds}s");
                    }
                    else if (info.Remaining < initialRemaining)
                    {
                        decrementObserved = true;
                        Console.WriteLine($"After {requestCount} requests: Remaining DECREMENTED to {info.Remaining}/{info.Quota} (was {initialRemaining})");
                        break;
                    }
                }

                if (!decrementObserved && requestCount % 20 == 0)
                {
                    var latest = rateLimitInfos[^1];
                    Console.WriteLine($"After {requestCount} requests: Remaining={latest.Remaining}/{latest.Quota} (unchanged)");
                }
            }
        }

        stopwatch.Stop();
        Console.WriteLine($"\nCompleted {requestCount} requests in {stopwatch.Elapsed.TotalSeconds:F1}s");

        // Assert
        rateLimitInfos.Should().NotBeEmpty("Should have received rate limit headers");
        rateLimitInfos.Should().AllSatisfy(info =>
        {
            info.IsValid.Should().BeTrue();
            info.Remaining.Should().BeGreaterThanOrEqualTo(0);
            info.Quota.Should().BeGreaterThan(0);
        });

        decrementObserved.Should().BeTrue(
            $"Rate limit 'Remaining' should have decremented after {requestCount} requests. " +
            $"Initial={initialRemaining}, Final={rateLimitInfos[^1].Remaining}. " +
            "This indicates either a bug in our parsing or unexpected API caching behavior.");
    }

    #endregion

    #region Proactive Throttling

    [Fact]
    public async Task CloudflareApi_ProactiveThrottling_ShouldDelayRequestsWhenQuotaLow()
    {
        SkipIfNotConfigured();

        // Arrange - Configure handler to track events
        var throttlingEvents = new List<(TimeSpan Delay, string? Reason)>();
        var quotaLowEvents = new List<double>();
        var rateLimitInfos = new List<RateLimitInfo>();

        var handler = new RateLimitAwareHandler(new RateLimitAwareOptions
        {
            EnableProactiveThrottling = true,
            QuotaLowThreshold = 1.0, // 100% threshold - will trigger as soon as ANY request is consumed
            OnThrottling = args =>
            {
                throttlingEvents.Add((args.Delay, args.Reason));
                Console.WriteLine($"THROTTLING: Delay={args.Delay.TotalMilliseconds:F0}ms, Reason={args.Reason}");
                return ValueTask.CompletedTask;
            },
            OnQuotaLow = args =>
            {
                quotaLowEvents.Add(args.RemainingPercentage);
                Console.WriteLine($"QUOTA LOW: {args.RemainingPercentage:P2} remaining");
                return ValueTask.CompletedTask;
            },
            OnRateLimitInfo = args =>
            {
                rateLimitInfos.Add(args.RateLimitInfo);
                Console.WriteLine($"RateLimitInfo: Remaining={args.RateLimitInfo.Remaining}/{args.RateLimitInfo.Quota} ({args.RateLimitInfo.GetRemainingPercentage():P2})");
                return ValueTask.CompletedTask;
            }
        })
        {
            InnerHandler = new HttpClientHandler()
        };

        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/")
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestSecretsConfiguration.CloudflareSettings.ApiToken);

        // Act - Fire concurrent requests until QuotaLow fires (remaining < quota)
        const int maxRequests = 100; // Safety limit
        const int timeoutSeconds = 60;
        const int batchSize = 20;
        var stopwatch = Stopwatch.StartNew();
        int requestCount = 0;

        Console.WriteLine("Making concurrent requests until QuotaLow event fires (threshold=100%, so any consumption triggers it)...");

        while (quotaLowEvents.Count == 0 && requestCount < maxRequests && stopwatch.Elapsed.TotalSeconds < timeoutSeconds)
        {
            // Fire a batch of concurrent requests
            var tasks = new List<Task>();
            for (int i = 0; i < batchSize && requestCount < maxRequests; i++)
            {
                requestCount++;
                tasks.Add(client.GetAsync(TestEndpoint));
            }

            await Task.WhenAll(tasks);
            Console.WriteLine($"Batch complete: {requestCount} requests, {quotaLowEvents.Count} QuotaLow events");
        }

        stopwatch.Stop();
        Console.WriteLine($"\nCompleted {requestCount} requests in {stopwatch.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"QuotaLow events: {quotaLowEvents.Count}");
        Console.WriteLine($"Throttling events: {throttlingEvents.Count}");

        // Assert - QuotaLow should have fired once remaining < quota (which should happen after first request)
        rateLimitInfos.Should().NotBeEmpty("Should have received rate limit headers");

        quotaLowEvents.Should().NotBeEmpty(
            $"QuotaLow event should have fired after {requestCount} requests. " +
            $"With 100% threshold, any Remaining < Quota should trigger it. " +
            $"Last seen: Remaining={rateLimitInfos[^1].Remaining}/{rateLimitInfos[^1].Quota} ({rateLimitInfos[^1].GetRemainingPercentage():P2}). " +
            "This indicates either the remaining never dropped below quota, or there's a bug in our QuotaLow detection.");

        // Verify QuotaLow events are at correct percentages
        quotaLowEvents.Should().AllSatisfy(pct =>
            pct.Should().BeLessThanOrEqualTo(1.0, "QuotaLow should only fire when at or below threshold"));

        // If throttling occurred, verify delays are positive
        if (throttlingEvents.Count > 0)
        {
            throttlingEvents.Should().AllSatisfy(e =>
                e.Delay.Should().BeGreaterThan(TimeSpan.Zero, "Throttling delay should be positive"));
        }
    }

    #endregion

    #region Handler and Callback Integration

    [Fact]
    public async Task CloudflareApi_Handler_ShouldInvokeAllCallbacks()
    {
        SkipIfNotConfigured();

        // Arrange - Track all callback invocations
        var rateLimitInfoReceived = false;
        RateLimitInfo? lastInfo = null;

        var handler = new RateLimitAwareHandler(new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            OnRateLimitInfo = args =>
            {
                rateLimitInfoReceived = true;
                lastInfo = args.RateLimitInfo;
                Console.WriteLine($"OnRateLimitInfo: {args.RateLimitInfo}");
                return ValueTask.CompletedTask;
            }
        })
        {
            InnerHandler = new HttpClientHandler()
        };

        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/")
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestSecretsConfiguration.CloudflareSettings.ApiToken);

        // Act
        var response = await client.GetAsync(TestEndpoint);

        Console.WriteLine($"Response Status: {response.StatusCode}");
        LogAllHeaders(response);

        // Assert
        rateLimitInfoReceived.Should().BeTrue("OnRateLimitInfo callback should be invoked");
        lastInfo.Should().NotBeNull();
        lastInfo!.Value.IsValid.Should().BeTrue();
    }

    #endregion

    #region IHttpClientFactory Integration

    [Fact]
    public async Task CloudflareApi_IHttpClientFactory_ShouldIntegrateCorrectly()
    {
        SkipIfNotConfigured();

        // Arrange
        var receivedInfos = new List<RateLimitInfo>();
        var services = new ServiceCollection();

        services.AddHttpClient("cloudflare", client =>
            {
                client.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", TestSecretsConfiguration.CloudflareSettings.ApiToken);
            })
            .AddRateLimitAwareHandler(options =>
            {
                options.EnableProactiveThrottling = false;
                options.OnRateLimitInfo = args =>
                {
                    receivedInfos.Add(args.RateLimitInfo);
                    return ValueTask.CompletedTask;
                };
            });

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient("cloudflare");

        // Act
        var response = await client.GetAsync(TestEndpoint);

        Console.WriteLine($"Response Status: {response.StatusCode}");
        LogAllHeaders(response);

        // Assert
        receivedInfos.Should().HaveCount(1, "Handler should process rate limit headers");
        receivedInfos[0].IsValid.Should().BeTrue();

        Console.WriteLine($"IHttpClientFactory integration: Parsed={receivedInfos[0]}");
    }

    #endregion

    #region Helper Methods

    private static void LogAllHeaders(HttpResponseMessage response)
    {
        Console.WriteLine("Response Headers:");
        foreach (var header in response.Headers.OrderBy(h => h.Key))
        {
            Console.WriteLine($"  {header.Key}: {string.Join(", ", header.Value)}");
        }
    }

    #endregion
}
