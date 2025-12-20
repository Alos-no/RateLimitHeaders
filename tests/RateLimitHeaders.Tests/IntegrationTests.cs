using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RateLimitHeaders.Http;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Tests.Fixtures;

namespace RateLimitHeaders.Tests;

public class IntegrationTests
{
    [Fact]
    public async Task TestServer_ShouldReturnRateLimitHeaders()
    {
        // Arrange
        await using var server = await RateLimitTestServer.CreateAsync(new RateLimitTestServerOptions
        {
            InitialQuota = 10,
            WindowSeconds = 60,
            PolicyName = "integration-test"
        });

        // Act
        var response = await server.Client.GetAsync("/api/test");
        var rateLimitInfo = RateLimitHeaderParser.Parse(response);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        rateLimitInfo.IsValid.Should().BeTrue();
        rateLimitInfo.PolicyName.Should().Be("integration-test");
        rateLimitInfo.Remaining.Should().Be(9);  // 10 - 1 request
        rateLimitInfo.Quota.Should().Be(10);
        rateLimitInfo.WindowSeconds.Should().Be(60);
    }

    [Fact]
    public async Task TestServer_ShouldDecrementRemainingOnEachRequest()
    {
        // Arrange
        await using var server = await RateLimitTestServer.CreateAsync(new RateLimitTestServerOptions
        {
            InitialQuota = 5,
            WindowSeconds = 60
        });

        // Act
        var responses = new List<RateLimitInfo>();
        for (int i = 0; i < 3; i++)
        {
            var response = await server.Client.GetAsync("/api/test");
            responses.Add(RateLimitHeaderParser.Parse(response));
        }

        // Assert
        responses[0].Remaining.Should().Be(4);  // 5 - 1
        responses[1].Remaining.Should().Be(3);  // 5 - 2
        responses[2].Remaining.Should().Be(2);  // 5 - 3
    }

    [Fact]
    public async Task TestServer_ShouldReturn429WhenQuotaExhausted()
    {
        // Arrange
        await using var server = await RateLimitTestServer.CreateAsync(new RateLimitTestServerOptions
        {
            InitialQuota = 2,
            Return429WhenExhausted = true,
            RetryAfterSeconds = 30
        });

        // Act - exhaust quota
        await server.Client.GetAsync("/api/test");  // 1 remaining
        await server.Client.GetAsync("/api/test");  // 0 remaining
        var response = await server.Client.GetAsync("/api/test");  // Should be 429

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.Contains("Retry-After").Should().BeTrue();
    }

    [Fact]
    public async Task Handler_WithTestServer_ShouldTrackRateLimits()
    {
        // Arrange
        await using var server = await RateLimitTestServer.CreateAsync(new RateLimitTestServerOptions
        {
            InitialQuota = 100,
            WindowSeconds = 60
        });

        var receivedInfos = new List<RateLimitInfo>();
        var handler = new RateLimitAwareHandler(new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            OnRateLimitInfo = args => { receivedInfos.Add(args.RateLimitInfo); return ValueTask.CompletedTask; }
        })
        {
            InnerHandler = server.Client.GetHandler()!
        };

        var client = new HttpClient(handler)
        {
            BaseAddress = server.Client.BaseAddress
        };

        // Act
        await client.GetAsync("/api/endpoint1");
        await client.GetAsync("/api/endpoint2");
        await client.GetAsync("/api/endpoint3");

        // Assert
        receivedInfos.Should().HaveCount(3);
        receivedInfos.Select(i => i.Remaining).Should().BeEquivalentTo([99, 98, 97]);
    }

    [Fact]
    public async Task Handler_WithTestServer_ShouldInvokeQuotaLowCallback()
    {
        // Arrange
        await using var server = await RateLimitTestServer.CreateAsync(new RateLimitTestServerOptions
        {
            InitialQuota = 10,
            WindowSeconds = 60
        });

        var quotaLowCount = 0;
        var handler = new RateLimitAwareHandler(new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            QuotaLowThreshold = 0.2,  // 20%
            OnQuotaLow = _ => { quotaLowCount++; return ValueTask.CompletedTask; }
        })
        {
            InnerHandler = server.Client.GetHandler()!
        };

        var client = new HttpClient(handler)
        {
            BaseAddress = server.Client.BaseAddress
        };

        // Act - make 9 requests (10% remaining = 1)
        for (int i = 0; i < 9; i++)
        {
            await client.GetAsync("/api/test");
        }

        // Assert - quota low callback should fire when reaching 20% threshold
        // Quota low at: 2/10=20%, 1/10=10%
        quotaLowCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ServiceCollection_WithTestServer_ShouldConfigureHandler()
    {
        // Arrange
        await using var server = await RateLimitTestServer.CreateAsync(new RateLimitTestServerOptions
        {
            InitialQuota = 100,
            WindowSeconds = 60
        });

        var receivedInfos = new List<RateLimitInfo>();
        var services = new ServiceCollection();
        services.AddHttpClient("test", client =>
        {
            client.BaseAddress = server.Client.BaseAddress;
        })
        .ConfigurePrimaryHttpMessageHandler(() => server.Client.GetHandler()!)
        .AddRateLimitAwareHandler(options =>
        {
            options.EnableProactiveThrottling = false;
            options.OnRateLimitInfo = args => { receivedInfos.Add(args.RateLimitInfo); return ValueTask.CompletedTask; };
        });

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("test");

        // Act
        await client.GetAsync("/api/test");

        // Assert
        receivedInfos.Should().HaveCount(1);
        receivedInfos[0].Remaining.Should().Be(99);
    }
}

/// <summary>
/// Extension to get the underlying handler from an HttpClient created by TestServer.
/// </summary>
internal static class HttpClientExtensions
{
    public static HttpMessageHandler? GetHandler(this HttpClient client)
    {
        // TestServer's CreateClient returns an HttpClient with a custom handler
        // We need to access it for composing with our handler
        var field = typeof(HttpMessageInvoker).GetField("_handler",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return field?.GetValue(client) as HttpMessageHandler;
    }
}
