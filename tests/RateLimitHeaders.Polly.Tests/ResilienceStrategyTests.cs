using System.Net;
using Polly;
using RateLimitHeaders.Events;
using RateLimitHeaders.Http;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Polly;
using RateLimitHeaders.Tests.Fixtures;
using RateLimitHeaders.Throttling;

namespace RateLimitHeaders.Polly.Tests;

/// <summary>
/// Tests for the RateLimitHeaders resilience strategy, covering:
/// - Quota low threshold at-or-below behavior
/// - Callback error handling
/// - Retry-After header support
/// - Per-endpoint tracking
/// - Custom StateKeyExtractor
/// </summary>
public class ResilienceStrategyTests
{
    #region Basic Strategy Tests

    [Fact]
    public async Task Strategy_WithRateLimitHeaders_ShouldParseAndStoreInContext()
    {
        // Arrange
        RateLimitInfo? capturedInfo = null;
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.OnRateLimitInfo = args =>
                {
                    capturedInfo = args.RateLimitInfo;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60, "test-policy");
        var client = new HttpClient(mockHandler);

        // Act
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context,
            client);

        // Assert
        capturedInfo.Should().NotBeNull();
        capturedInfo!.Value.PolicyName.Should().Be("test-policy");
        capturedInfo.Value.Remaining.Should().Be(50);
        capturedInfo.Value.Quota.Should().Be(100);

        context.Properties.TryGetValue(RateLimitContextProperties.RateLimitInfoKey, out var storedInfo);
        storedInfo.PolicyName.Should().Be("test-policy");

        ResilienceContextPool.Shared.Return(context);
    }

    #endregion

    #region Quota Low Threshold Tests

    [Fact]
    public async Task Strategy_QuotaExactlyAtThreshold_ShouldInvokeQuotaLowCallback()
    {
        // Arrange - This tests the fix: quota AT threshold should trigger callback
        OnQuotaLowArguments? receivedArgs = null;
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.QuotaLowThreshold = 0.10;  // 10%
                options.OnQuotaLow = args =>
                {
                    receivedArgs = args;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(10, 30, 100, 60);  // Exactly 10%
        var client = new HttpClient(mockHandler);

        // Act
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context,
            client);

        // Assert - Should trigger at exactly 10%
        receivedArgs.Should().NotBeNull("quota at threshold should trigger callback");
        receivedArgs!.Value.QuotaPercentage.Should().Be(0.10);
        receivedArgs.Value.Threshold.Should().Be(0.10);

        ResilienceContextPool.Shared.Return(context);
    }

    [Fact]
    public async Task Strategy_QuotaBelowThreshold_ShouldInvokeQuotaLowCallback()
    {
        // Arrange
        OnQuotaLowArguments? receivedArgs = null;
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.QuotaLowThreshold = 0.10;  // 10%
                options.OnQuotaLow = args =>
                {
                    receivedArgs = args;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(5, 30, 100, 60);  // 5% - below threshold
        var client = new HttpClient(mockHandler);

        // Act
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context,
            client);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Value.QuotaPercentage.Should().Be(0.05);

        ResilienceContextPool.Shared.Return(context);
    }

    [Fact]
    public async Task Strategy_QuotaAboveThreshold_ShouldNotInvokeQuotaLowCallback()
    {
        // Arrange
        OnQuotaLowArguments? receivedArgs = null;
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.QuotaLowThreshold = 0.10;  // 10%
                options.OnQuotaLow = args =>
                {
                    receivedArgs = args;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);  // 50% - well above threshold
        var client = new HttpClient(mockHandler);

        // Act
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context,
            client);

        // Assert
        receivedArgs.Should().BeNull();

        ResilienceContextPool.Shared.Return(context);
    }

    [Fact]
    public async Task Strategy_OnQuotaLowArguments_ShouldContainThreshold()
    {
        // Arrange
        OnQuotaLowArguments? receivedArgs = null;
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.QuotaLowThreshold = 0.15;  // 15%
                options.OnQuotaLow = args =>
                {
                    receivedArgs = args;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(10, 30, 100, 60);  // 10% - below 15%
        var client = new HttpClient(mockHandler);

        // Act
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context,
            client);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Value.Threshold.Should().Be(0.15);
        receivedArgs.Value.QuotaPercentage.Should().Be(0.10);

        ResilienceContextPool.Shared.Return(context);
    }

    #endregion

    #region Callback Error Handling Tests

    [Fact]
    public async Task Strategy_WithOnRateLimitInfoException_ShouldNotPropagateException()
    {
        // Arrange
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.OnRateLimitInfo = _ => throw new InvalidOperationException("Callback error");
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);
        var client = new HttpClient(mockHandler);

        // Act - Should not throw
        var context = ResilienceContextPool.Shared.Get();
        var response = await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context,
            client);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ResilienceContextPool.Shared.Return(context);
    }

    [Fact]
    public async Task Strategy_WithOnQuotaLowException_ShouldNotPropagateException()
    {
        // Arrange
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.QuotaLowThreshold = 0.5;  // High threshold to trigger
                options.OnQuotaLow = _ => throw new InvalidOperationException("Callback error");
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(10, 30, 100, 60);  // 10% - triggers quota low
        var client = new HttpClient(mockHandler);

        // Act - Should not throw
        var context = ResilienceContextPool.Shared.Get();
        var response = await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context,
            client);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ResilienceContextPool.Shared.Return(context);
    }

    [Fact]
    public async Task Strategy_WithOnThrottlingException_ShouldNotPropagateException()
    {
        // Arrange
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = true;
                options.TrackStatePerEndpoint = false;
                options.ThrottlingAlgorithm = new AlwaysThrottleAlgorithm();
                options.OnThrottling = _ => throw new InvalidOperationException("Callback error");
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);
        var client = new HttpClient(mockHandler);

        // Act - First request to populate state
        var context1 = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context1,
            client);
        ResilienceContextPool.Shared.Return(context1);

        // Second request triggers throttling - should not throw
        var context2 = ResilienceContextPool.Shared.Get();
        var response = await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context2,
            client);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ResilienceContextPool.Shared.Return(context2);
    }

    #endregion

    #region Retry-After Support Tests

    [Fact]
    public async Task Strategy_With429AndRetryAfter_ShouldOverrideResetSeconds()
    {
        // Arrange
        RateLimitInfo? capturedInfo = null;
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.OnRateLimitInfo = args =>
                {
                    capturedInfo = args.RateLimitInfo;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        // Queue 429 with Retry-After=120, but RateLimit says t=30
        mockHandler.QueueTooManyRequestsResponse(retryAfterSeconds: 120, remaining: 0, resetSeconds: 30);
        var client = new HttpClient(mockHandler);

        // Act
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context,
            client);

        // Assert - Retry-After (120) takes precedence over t=30
        capturedInfo.Should().NotBeNull();
        capturedInfo!.Value.ResetSeconds.Should().Be(120);
        capturedInfo.Value.Remaining.Should().Be(0);

        ResilienceContextPool.Shared.Return(context);
    }

    [Fact]
    public async Task Strategy_With429AndRetryAfterOnly_ShouldUpdateState()
    {
        // Arrange - Test Retry-After only (no RateLimit headers)
        RateLimitInfo? capturedInfo = null;
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.OnRateLimitInfo = args =>
                {
                    capturedInfo = args.RateLimitInfo;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        // 429 with only Retry-After, no RateLimit headers
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("Retry-After", "60");
        mockHandler.QueueResponse(response);
        var client = new HttpClient(mockHandler);

        // Act
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context,
            client);

        // Assert - Should NOT invoke callback for Retry-After only
        capturedInfo.Should().BeNull();

        ResilienceContextPool.Shared.Return(context);
    }

    [Fact]
    public async Task Strategy_With503AndRetryAfter_ShouldOverrideResetSeconds()
    {
        // Arrange
        RateLimitInfo? capturedInfo = null;
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.OnRateLimitInfo = args =>
                {
                    capturedInfo = args.RateLimitInfo;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.Add("Retry-After", "300");
        response.Headers.Add("RateLimit", "\"default\";r=0;t=60");
        response.Headers.Add("RateLimit-Policy", "\"default\";q=100;w=60");
        mockHandler.QueueResponse(response);
        var client = new HttpClient(mockHandler);

        // Act
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context,
            client);

        // Assert - Retry-After (300) overrides t=60
        capturedInfo.Should().NotBeNull();
        capturedInfo!.Value.ResetSeconds.Should().Be(300);

        ResilienceContextPool.Shared.Return(context);
    }

    [Fact]
    public async Task Strategy_With200AndRetryAfter_ShouldNotUseRetryAfter()
    {
        // Arrange - Retry-After on 200 OK should be ignored
        RateLimitInfo? capturedInfo = null;
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.OnRateLimitInfo = args =>
                {
                    capturedInfo = args.RateLimitInfo;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        var response = MockHttpHandler.CreateRateLimitResponse(HttpStatusCode.OK, 50, 30, 100, 60);
        response.Headers.Add("Retry-After", "120");
        mockHandler.QueueResponse(response);
        var client = new HttpClient(mockHandler);

        // Act
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context,
            client);

        // Assert - Should use RateLimit header value (30), not Retry-After (120)
        capturedInfo.Should().NotBeNull();
        capturedInfo!.Value.ResetSeconds.Should().Be(30);
        capturedInfo.Value.Remaining.Should().Be(50);

        ResilienceContextPool.Shared.Return(context);
    }

    #endregion

    #region Per-Endpoint Tracking Tests

    [Fact]
    public async Task Strategy_WithPerEndpointTracking_ShouldTrackDifferentEndpointsSeparately()
    {
        // Arrange
        var capturedInfos = new List<(string Endpoint, RateLimitInfo Info)>();
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.TrackStatePerEndpoint = true;
                options.OnRateLimitInfo = args =>
                {
                    capturedInfos.Add((args.Response.RequestMessage?.RequestUri?.ToString() ?? "", args.RateLimitInfo));
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        // Different endpoints with different rate limits
        mockHandler.SetResponseFactory(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            return path switch
            {
                "/api/users" => MockHttpHandler.CreateRateLimitResponse(HttpStatusCode.OK, 100, 60, 1000, 60, "users-policy"),
                "/api/orders" => MockHttpHandler.CreateRateLimitResponse(HttpStatusCode.OK, 50, 30, 100, 60, "orders-policy"),
                _ => MockHttpHandler.CreateRateLimitResponse(HttpStatusCode.OK, 25, 15, 50, 60, "default-policy")
            };
        });
        var client = new HttpClient(mockHandler);

        // Act
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://api.example.com/api/users", ctx.CancellationToken),
            context,
            client);
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://api.example.com/api/orders", ctx.CancellationToken),
            context,
            client);
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://api.example.com/api/products", ctx.CancellationToken),
            context,
            client);

        // Assert
        capturedInfos.Should().HaveCount(3);
        capturedInfos[0].Info.PolicyName.Should().Be("users-policy");
        capturedInfos[0].Info.Remaining.Should().Be(100);
        capturedInfos[1].Info.PolicyName.Should().Be("orders-policy");
        capturedInfos[1].Info.Remaining.Should().Be(50);
        capturedInfos[2].Info.PolicyName.Should().Be("default-policy");
        capturedInfos[2].Info.Remaining.Should().Be(25);

        ResilienceContextPool.Shared.Return(context);
    }

    [Fact]
    public async Task Strategy_WithPerEndpointTrackingDisabled_ShouldUseGlobalState()
    {
        // Arrange
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.TrackStatePerEndpoint = false;  // Use global state
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.SetResponseFactory(_ =>
            MockHttpHandler.CreateRateLimitResponse(HttpStatusCode.OK, 50, 30, 100, 60));
        var client = new HttpClient(mockHandler);

        // Act - requests to different endpoints should all use "global" key
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://api.example.com/api/users", ctx.CancellationToken),
            context,
            client);
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://api.example.com/api/orders", ctx.CancellationToken),
            context,
            client);

        // Assert - This test mainly verifies no exceptions with global tracking
        ResilienceContextPool.Shared.Return(context);
    }

    [Fact]
    public void Strategy_CustomStateKeyExtractor_ShouldBeConfigurable()
    {
        // Arrange - Verify that custom StateKeyExtractor can be configured without errors
        // Note: For the strategy, proactive throttling needs the request in context
        // (via RateLimitContextProperties.RequestMessageKey), so full integration testing
        // of per-endpoint proactive throttling is better done via the Handler.
        // This test verifies configuration works correctly.

        var options = new RateLimitHeadersStrategyOptions
        {
            EnableProactiveThrottling = true,
            TrackStatePerEndpoint = true,
            StateKeyExtractor = request => $"custom:{request.RequestUri?.Host}"
        };

        // Act - Verify GetStateKey uses the custom extractor
        var request = new HttpRequestMessage(HttpMethod.Get, "http://api.example.com/test");
        var key = options.GetStateKey(request);

        // Assert
        key.Should().Be("custom:api.example.com");
    }

    [Fact]
    public void Strategy_GetStateKey_WithNullRequest_ShouldReturnGlobal()
    {
        // Arrange
        var options = new RateLimitHeadersStrategyOptions
        {
            TrackStatePerEndpoint = true
        };

        // Act
        var key = options.GetStateKey(null);

        // Assert
        key.Should().Be("global");
    }

    [Fact]
    public void Strategy_GetStateKey_WithNullReturningExtractor_ShouldReturnGlobal()
    {
        // Arrange
        var options = new RateLimitHeadersStrategyOptions
        {
            TrackStatePerEndpoint = true,
            StateKeyExtractor = _ => null!  // Custom extractor that returns null
        };

        var request = new HttpRequestMessage(HttpMethod.Get, "http://api.example.com/test");

        // Act
        var key = options.GetStateKey(request);

        // Assert - Should fall back to global when custom extractor returns null
        key.Should().Be("global");
    }

    [Fact]
    public void Strategy_GetStateKey_WithTrackingDisabled_ShouldReturnGlobal()
    {
        // Arrange
        var options = new RateLimitHeadersStrategyOptions
        {
            TrackStatePerEndpoint = false
        };

        var request = new HttpRequestMessage(HttpMethod.Get, "http://api.example.com/v1/users");

        // Act
        var key = options.GetStateKey(request);

        // Assert
        key.Should().Be("global");
    }

    [Fact]
    public void Strategy_GetStateKey_DefaultExtractor_ShouldUseSchemeHostAndPort()
    {
        // Arrange
        var options = new RateLimitHeadersStrategyOptions
        {
            TrackStatePerEndpoint = true
            // No custom StateKeyExtractor - use default
        };

        var request = new HttpRequestMessage(HttpMethod.Get, "http://api.example.com/v1/users/123");

        // Act
        var key = options.GetStateKey(request);

        // Assert - scheme://host:port, so ports and schemes of one host never share an entry (AUD-29)
        key.Should().Be("http://api.example.com:80");
    }

    [Fact]
    public async Task Strategy_DefaultStateKeyExtractor_ShouldUseHostnameOnly()
    {
        // Arrange
        var capturedInfos = new List<(string Endpoint, string PolicyName)>();
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.TrackStatePerEndpoint = true;
                // No custom StateKeyExtractor - use default
                options.OnRateLimitInfo = args =>
                {
                    capturedInfos.Add((
                        args.Response.RequestMessage?.RequestUri?.ToString() ?? "",
                        args.RateLimitInfo.PolicyName));
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        int callCount = 0;
        mockHandler.SetResponseFactory(request =>
        {
            callCount++;
            return MockHttpHandler.CreateRateLimitResponse(
                HttpStatusCode.OK, 100 - callCount, 30, 100, 60, $"policy-{callCount}");
        });
        var client = new HttpClient(mockHandler);

        // Act - same hostname should share state (all paths on same host)
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://api.example.com/v1/users", ctx.CancellationToken),
            context,
            client);
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://api.example.com/v1/orders", ctx.CancellationToken),
            context,
            client);
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://api.example.com/v2/products", ctx.CancellationToken),
            context,
            client);

        // Assert - all three should get their respective policies
        capturedInfos.Should().HaveCount(3);
        capturedInfos[0].PolicyName.Should().Be("policy-1");
        capturedInfos[1].PolicyName.Should().Be("policy-2");
        capturedInfos[2].PolicyName.Should().Be("policy-3");

        ResilienceContextPool.Shared.Return(context);
    }

    #endregion

    #region Multiple Policies Tests

    [Fact]
    public void Parser_WithMultiplePolicies_ShouldSelectLowestRemainingCount()
    {
        // Arrange - Test that parser matches policies by name and selects the most restrictive
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"burst\";r=50;t=30, \"daily\";r=900;t=43200");
        response.Headers.Add("RateLimit-Policy", "\"burst\";q=100;w=60, \"daily\";q=10000;w=86400");

        // Act
        var info = RateLimitHeaderParser.Parse(response);

        // Assert - the six-rung comparator (task T5 in PLAN-audit-fixes.md) compares raw
        // remaining counts before remaining fractions, so burst (50 left) beats daily (900
        // left) even though daily's fraction is lower; 50 requests left is what actually
        // bounds the caller
        info.IsValid.Should().BeTrue();
        info.PolicyName.Should().Be("burst");
        info.Remaining.Should().Be(50);
        info.Quota.Should().Be(100);
    }

    [Fact]
    public async Task Strategy_WithNonDefaultPolicy_ShouldParseCorrectly()
    {
        // Arrange
        RateLimitInfo? capturedInfo = null;
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.OnRateLimitInfo = args =>
                {
                    capturedInfo = args.RateLimitInfo;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(75, 45, 500, 120, "premium-tier");
        var client = new HttpClient(mockHandler);

        // Act
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context,
            client);

        // Assert
        capturedInfo.Should().NotBeNull();
        capturedInfo!.Value.PolicyName.Should().Be("premium-tier");
        capturedInfo.Value.Remaining.Should().Be(75);
        capturedInfo.Value.Quota.Should().Be(500);
        capturedInfo.Value.WindowSeconds.Should().Be(120);

        ResilienceContextPool.Shared.Return(context);
    }

    #endregion

    #region RequestMessageKey Tests

    [Fact]
    public async Task Strategy_WithRequestMessageKey_ShouldUseRequestForStateKeyLookup()
    {
        // Arrange - This tests that setting RateLimitContextProperties.RequestMessageKey
        // enables per-endpoint state tracking during proactive throttling.
        // The key is used to look up state when deciding whether to throttle.
        var throttlingCount = 0;
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = true;
                options.TrackStatePerEndpoint = true;
                options.ThrottlingAlgorithm = new AlwaysThrottleAlgorithm();
                options.OnThrottling = _ =>
                {
                    throttlingCount++;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);
        var client = new HttpClient(mockHandler);

        // Act - First request with RequestMessageKey set
        var context1 = ResilienceContextPool.Shared.Get();
        var request1 = new HttpRequestMessage(HttpMethod.Get, "http://api.example.com/test");
        context1.Properties.Set(RateLimitContextProperties.RequestMessageKey, request1);
        await pipeline.ExecuteAsync(
            async (ctx) =>
            {
                var req = ctx.Properties.GetValue(RateLimitContextProperties.RequestMessageKey, null!);
                return await client.SendAsync(req, ctx.CancellationToken);
            },
            context1);
        ResilienceContextPool.Shared.Return(context1);

        // Second request with same host - should use same state key and trigger throttling
        var context2 = ResilienceContextPool.Shared.Get();
        var request2 = new HttpRequestMessage(HttpMethod.Get, "http://api.example.com/another");
        context2.Properties.Set(RateLimitContextProperties.RequestMessageKey, request2);
        await pipeline.ExecuteAsync(
            async (ctx) =>
            {
                var req = ctx.Properties.GetValue(RateLimitContextProperties.RequestMessageKey, null!);
                return await client.SendAsync(req, ctx.CancellationToken);
            },
            context2);
        ResilienceContextPool.Shared.Return(context2);

        // Assert - Throttling should have been triggered on second request
        // because state was stored using the first request's state key
        throttlingCount.Should().Be(1);
    }

    // The test that stood here (Strategy_WithoutRequestMessageKey_AndPerEndpointTracking_
    // CannotDoProactiveThrottling) pinned the Critical defect AUD-02 from
    // TRACKER-adversarial-audit.md: with no request on the context, the pre-request lookup
    // could never find the per-endpoint state and the strategy never throttled. The strategy
    // now falls back to the most recently observed endpoint; the replacement scenario is
    // ReadmePipelineShape_DelaysTheSecondRequest in AuditFixes\StateKeyResolutionTests.cs (POLLY05).

    [Fact]
    public async Task Strategy_WithoutRequestMessageKey_AndGlobalTracking_ShouldThrottle()
    {
        // Arrange - When TrackStatePerEndpoint is false (global tracking),
        // proactive throttling works without RequestMessageKey because both
        // storage and lookup use the "global" key.
        var throttlingCalls = 0;
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = true;
                options.TrackStatePerEndpoint = false; // Global tracking
                options.ThrottlingAlgorithm = new AlwaysThrottleAlgorithm();
                options.OnThrottling = _ =>
                {
                    throttlingCalls++;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);
        var client = new HttpClient(mockHandler);

        // Act - First request populates global state
        var context1 = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://api.example.com/test", ctx.CancellationToken),
            context1,
            client);
        ResilienceContextPool.Shared.Return(context1);

        // Second request - proactive throttling finds global state
        var context2 = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://api.example.com/test2", ctx.CancellationToken),
            context2,
            client);
        ResilienceContextPool.Shared.Return(context2);

        // Assert - Throttling should work with global tracking
        throttlingCalls.Should().Be(1);
    }

    [Fact]
    public async Task Strategy_RequestMessageKey_ShouldBeStoredInContextAfterExecution()
    {
        // Arrange
        HttpRequestMessage? storedRequest = null;
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);
        var client = new HttpClient(mockHandler);

        // Act
        var context = ResilienceContextPool.Shared.Get();
        var request = new HttpRequestMessage(HttpMethod.Get, "http://example.com/test");
        context.Properties.Set(RateLimitContextProperties.RequestMessageKey, request);
        await pipeline.ExecuteAsync(
            async (ctx) =>
            {
                var req = ctx.Properties.GetValue(RateLimitContextProperties.RequestMessageKey, null!);
                return await client.SendAsync(req, ctx.CancellationToken);
            },
            context);

        // After execution, request should still be accessible
        context.Properties.TryGetValue(RateLimitContextProperties.RequestMessageKey, out storedRequest);
        ResilienceContextPool.Shared.Return(context);

        // Assert
        storedRequest.Should().BeSameAs(request);
    }

    #endregion

    #region Helper Classes

    private sealed class AlwaysThrottleAlgorithm : IThrottlingAlgorithm
    {
        public ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo)
        {
            if (!rateLimitInfo.IsValid)
            {
                return ThrottlingResult.NoThrottle;
            }

            return ThrottlingResult.Throttle(TimeSpan.FromMilliseconds(10), "Always throttle for testing");
        }
    }

    #endregion
}
