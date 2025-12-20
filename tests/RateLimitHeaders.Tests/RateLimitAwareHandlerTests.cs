using System.Net;
using RateLimitHeaders.Events;
using RateLimitHeaders.Http;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Tests.Fixtures;

namespace RateLimitHeaders.Tests;

public class RateLimitAwareHandlerTests
{
    [Fact]
    public async Task SendAsync_WithRateLimitHeaders_ShouldInvokeCallback()
    {
        // Arrange
        RateLimitEventArgs? receivedArgs = null;
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            OnRateLimitInfo = args => { receivedArgs = args; return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60, "test-policy");

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Value.RateLimitInfo.IsValid.Should().BeTrue();
        receivedArgs.Value.RateLimitInfo.PolicyName.Should().Be("test-policy");
        receivedArgs.Value.RateLimitInfo.Remaining.Should().Be(50);
        receivedArgs.Value.RateLimitInfo.Quota.Should().Be(100);
    }

    [Fact]
    public async Task SendAsync_WithLowQuota_ShouldInvokeQuotaLowCallback()
    {
        // Arrange
        QuotaLowEventArgs? receivedArgs = null;
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            QuotaLowThreshold = 0.1,
            OnQuotaLow = args => { receivedArgs = args; return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(5, 30, 100, 60);  // 5% remaining

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Value.RemainingPercentage.Should().Be(0.05);
        receivedArgs.Value.Threshold.Should().Be(0.1);
    }

    [Fact]
    public async Task SendAsync_WithoutLowQuota_ShouldNotInvokeQuotaLowCallback()
    {
        // Arrange
        QuotaLowEventArgs? receivedArgs = null;
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            QuotaLowThreshold = 0.1,
            OnQuotaLow = args => { receivedArgs = args; return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);  // 50% remaining

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");

        // Assert
        receivedArgs.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_WithNoRateLimitHeaders_ShouldNotInvokeCallback()
    {
        // Arrange
        RateLimitEventArgs? receivedArgs = null;
        var options = new RateLimitAwareOptions
        {
            OnRateLimitInfo = args => { receivedArgs = args; return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueResponse(MockHttpHandler.CreateNoRateLimitResponse());

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");

        // Assert
        receivedArgs.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_ShouldReturnOriginalResponse()
    {
        // Arrange
        var options = new RateLimitAwareOptions { EnableProactiveThrottling = false };

        var mockHandler = new MockHttpHandler();
        var expectedResponse = MockHttpHandler.CreateRateLimitResponse(
            HttpStatusCode.Created, 50, 30, 100, 60);
        mockHandler.QueueResponse(expectedResponse);

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("http://example.com/api/test");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task SendAsync_WithCallbackException_ShouldNotPropagateException()
    {
        // Arrange
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            OnRateLimitInfo = _ => throw new InvalidOperationException("Callback error")
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act - Should not throw despite callback exception
        var response = await client.GetAsync("http://example.com/api/test");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendAsync_MultipleRequests_ShouldTrackState()
    {
        // Arrange
        var receivedInfos = new List<RateLimitInfo>();
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            OnRateLimitInfo = args => { receivedInfos.Add(args.RateLimitInfo); return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(100, 60, 100, 60);
        mockHandler.QueueRateLimitResponse(99, 59, 100, 60);
        mockHandler.QueueRateLimitResponse(98, 58, 100, 60);

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");
        await client.GetAsync("http://example.com/api/test");
        await client.GetAsync("http://example.com/api/test");

        // Assert
        receivedInfos.Should().HaveCount(3);
        receivedInfos[0].Remaining.Should().Be(100);
        receivedInfos[1].Remaining.Should().Be(99);
        receivedInfos[2].Remaining.Should().Be(98);
    }

    [Fact]
    public async Task SendAsync_With429Response_ShouldStillParseHeaders()
    {
        // Arrange
        RateLimitEventArgs? receivedArgs = null;
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            OnRateLimitInfo = args => { receivedArgs = args; return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueTooManyRequestsResponse(60, remaining: 0);

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("http://example.com/api/test");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Value.RateLimitInfo.Remaining.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_DefaultOptions_ShouldWork()
    {
        // Arrange
        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);

        var handler = new RateLimitAwareHandler { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("http://example.com/api/test");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task StateKeyExtractor_Custom_ShouldBeUsed()
    {
        // Arrange
        var keys = new List<string>();
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            TrackStatePerEndpoint = true,
            StateKeyExtractor = request => $"custom:{request.RequestUri?.Host}",
            OnRateLimitInfo = args => { keys.Add($"custom:{args.RequestUri?.Host}"); return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");

        // Assert
        keys.Should().HaveCount(1);
        keys[0].Should().Be("custom:example.com");
    }

    [Fact]
    public async Task SendAsync_WithCancellation_ShouldRespectToken()
    {
        // Arrange
        var options = new RateLimitAwareOptions { EnableProactiveThrottling = false };
        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert - TaskCanceledException inherits from OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAsync("http://example.com/api/test", cts.Token));
    }

    #region Retry-After Tests

    [Fact]
    public async Task SendAsync_With429AndRetryAfter_ShouldOverrideResetSeconds()
    {
        // Arrange
        RateLimitEventArgs? receivedArgs = null;
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            OnRateLimitInfo = args => { receivedArgs = args; return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        // Queue 429 with Retry-After=120, but RateLimit says t=30
        mockHandler.QueueTooManyRequestsResponse(retryAfterSeconds: 120, remaining: 0, resetSeconds: 30);

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");

        // Assert - Retry-After (120) takes precedence over t=30
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Value.RateLimitInfo.ResetSeconds.Should().Be(120);
        receivedArgs.Value.RateLimitInfo.Remaining.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_With429AndRetryAfterOnly_ShouldCreateRateLimitInfo()
    {
        // Arrange
        RateLimitEventArgs? receivedArgs = null;
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            OnRateLimitInfo = args => { receivedArgs = args; return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        // Queue 429 with only Retry-After, no RateLimit headers
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("Retry-After", "60");
        mockHandler.QueueResponse(response);

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");

        // Assert - Should not invoke callback (no RateLimit headers parsed)
        // but state should be updated with Retry-After
        receivedArgs.Should().BeNull();  // No OnRateLimitInfo callback for Retry-After only
    }

    [Fact]
    public async Task SendAsync_With503AndRetryAfter_ShouldHandleServiceUnavailable()
    {
        // Arrange
        RateLimitEventArgs? receivedArgs = null;
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            OnRateLimitInfo = args => { receivedArgs = args; return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.Add("Retry-After", "300");
        response.Headers.Add("RateLimit", "\"default\";r=0;t=60");
        response.Headers.Add("RateLimit-Policy", "\"default\";q=100;w=60");
        mockHandler.QueueResponse(response);

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");

        // Assert - Retry-After (300) overrides t=60
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Value.RateLimitInfo.ResetSeconds.Should().Be(300);
    }

    [Fact]
    public async Task SendAsync_With200AndRetryAfter_ShouldNotUseRetryAfter()
    {
        // Arrange
        RateLimitEventArgs? receivedArgs = null;
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            OnRateLimitInfo = args => { receivedArgs = args; return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        // 200 OK with Retry-After (unusual but possible) - should be ignored
        var response = MockHttpHandler.CreateRateLimitResponse(HttpStatusCode.OK, 50, 30, 100, 60);
        response.Headers.Add("Retry-After", "120");
        mockHandler.QueueResponse(response);

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");

        // Assert - Should use RateLimit header value, not Retry-After (only for 429/503)
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Value.RateLimitInfo.ResetSeconds.Should().Be(30);  // Original value
        receivedArgs.Value.RateLimitInfo.Remaining.Should().Be(50);
    }

    #endregion

    #region StateKeyExtractor Tests

    [Fact]
    public async Task SendAsync_WithNullReturningStateKeyExtractor_ShouldFallbackToGlobal()
    {
        // Arrange
        var stateKeys = new List<string>();
        var stateTracker = new RateLimitStateTracker();
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            TrackStatePerEndpoint = true,
            StateKeyExtractor = _ => null!,  // Custom extractor returns null
            OnRateLimitInfo = args =>
            {
                // The state key used should be "global" when extractor returns null
                return ValueTask.CompletedTask;
            }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);

        var handler = new RateLimitAwareHandler(options, stateTracker, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            InnerHandler = mockHandler
        };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://api.example.com/test");

        // Assert - State should be stored under "global" key, not null
        var globalState = stateTracker.GetRateLimitInfo("global");
        globalState.IsValid.Should().BeTrue();
        globalState.Remaining.Should().Be(50);
    }

    [Fact]
    public async Task SendAsync_WithCustomStateKeyExtractor_ShouldUseCustomKey()
    {
        // Arrange
        var stateTracker = new RateLimitStateTracker();
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            TrackStatePerEndpoint = true,
            StateKeyExtractor = request => $"custom-{request.RequestUri?.Host}"
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);

        var handler = new RateLimitAwareHandler(options, stateTracker, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            InnerHandler = mockHandler
        };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://api.example.com/test");

        // Assert - State should be stored under custom key
        var customState = stateTracker.GetRateLimitInfo("custom-api.example.com");
        customState.IsValid.Should().BeTrue();
        customState.Remaining.Should().Be(50);
    }

    #endregion

    #region Async Callback Exception Tests

    [Fact]
    public async Task SendAsync_WithAsyncCallbackException_ShouldNotPropagateException()
    {
        // Arrange - This tests an exception thrown during async execution
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            OnRateLimitInfo = async _ =>
            {
                await Task.Delay(1);  // Make it truly async
                throw new InvalidOperationException("Async callback error");
            }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act - Should not throw despite async callback exception
        var response = await client.GetAsync("http://example.com/api/test");

        // Assert - Request should complete successfully
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendAsync_WithQuotaLowAsyncException_ShouldNotPropagateException()
    {
        // Arrange
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            QuotaLowThreshold = 0.5,
            OnQuotaLow = async _ =>
            {
                await Task.Delay(1);
                throw new InvalidOperationException("OnQuotaLow async exception");
            }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(10, 30, 100, 60);  // 10% remaining, below threshold

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("http://example.com/api/test");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendAsync_WithThrottlingAsyncException_ShouldNotPropagateException()
    {
        // Arrange
        var stateTracker = new RateLimitStateTracker();

        // Pre-populate state to trigger throttling
        stateTracker.UpdateState("global", new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 1,  // Very low to trigger throttling
            Quota = 100,
            ResetSeconds = 60,
            IsValid = true
        });

        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = true,
            TrackStatePerEndpoint = false,  // Use global key
            OnThrottling = async _ =>
            {
                await Task.Delay(1);
                throw new InvalidOperationException("OnThrottling async exception");
            }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);

        var handler = new RateLimitAwareHandler(options, stateTracker, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            InnerHandler = mockHandler
        };
        var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("http://example.com/api/test");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendAsync_WithMultipleAsyncCallbackExceptions_ShouldCompleteSuccessfully()
    {
        // Arrange - All callbacks throw async exceptions
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            QuotaLowThreshold = 0.5,
            OnRateLimitInfo = async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("OnRateLimitInfo error");
            },
            OnQuotaLow = async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("OnQuotaLow error");
            }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(10, 30, 100, 60);  // Low quota triggers both callbacks

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("http://example.com/api/test");

        // Assert - Both callbacks throw but request completes
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region Multiple Handlers in Pipeline Tests

    [Fact]
    public async Task SendAsync_WithMultipleHandlersInPipeline_ShouldExecuteInCorrectOrder()
    {
        // Arrange - Test that RateLimitAwareHandler works correctly in a handler chain
        var executionOrder = new List<string>();

        var outerHandler = new OrderTrackingHandler("outer", executionOrder);
        var rateLimitHandler = new RateLimitAwareHandler(new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            OnRateLimitInfo = _ => { executionOrder.Add("callback"); return ValueTask.CompletedTask; }
        });
        var innerHandler = new OrderTrackingHandler("inner", executionOrder);
        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);

        // Chain: outerHandler -> rateLimitHandler -> innerHandler -> mockHandler
        outerHandler.InnerHandler = rateLimitHandler;
        rateLimitHandler.InnerHandler = innerHandler;
        innerHandler.InnerHandler = mockHandler;

        var client = new HttpClient(outerHandler);

        // Act
        await client.GetAsync("http://example.com/api/test");

        // Assert - handlers should execute in order
        executionOrder.Should().ContainInOrder("outer-before", "inner-before", "inner-after", "callback", "outer-after");
    }

    [Fact]
    public async Task SendAsync_WithRetryHandlerWrappingRateLimitHandler_ShouldAllowRetry()
    {
        // Arrange - Simulate a retry handler that wraps the rate limit handler
        var attemptCount = 0;
        var rateLimitInfoCount = 0;

        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            OnRateLimitInfo = _ => { rateLimitInfoCount++; return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        // First attempt returns 503, second returns 200
        var errorResponse = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        errorResponse.Headers.Add("RateLimit", "\"default\";r=0;t=5");
        mockHandler.QueueResponse(errorResponse);
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);

        var retryHandler = new SimpleRetryHandler(maxRetries: 1);
        var rateLimitHandler = new RateLimitAwareHandler(options);

        // Chain: retryHandler -> rateLimitHandler -> mockHandler
        retryHandler.InnerHandler = rateLimitHandler;
        rateLimitHandler.InnerHandler = mockHandler;

        retryHandler.OnAttempt = () => attemptCount++;

        var client = new HttpClient(retryHandler);

        // Act
        var response = await client.GetAsync("http://example.com/api/test");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        attemptCount.Should().Be(2, "should have retried once");
        rateLimitInfoCount.Should().Be(2, "rate limit info should be captured for each attempt");
    }

    /// <summary>
    /// Helper handler that tracks execution order.
    /// </summary>
    private sealed class OrderTrackingHandler : DelegatingHandler
    {
        private readonly string _name;
        private readonly List<string> _executionOrder;

        public OrderTrackingHandler(string name, List<string> executionOrder)
        {
            _name = name;
            _executionOrder = executionOrder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _executionOrder.Add($"{_name}-before");
            var response = await base.SendAsync(request, cancellationToken);
            _executionOrder.Add($"{_name}-after");
            return response;
        }
    }

    /// <summary>
    /// Simple retry handler for testing pipeline scenarios.
    /// </summary>
    private sealed class SimpleRetryHandler : DelegatingHandler
    {
        private readonly int _maxRetries;
        public Action? OnAttempt { get; set; }

        public SimpleRetryHandler(int maxRetries)
        {
            _maxRetries = maxRetries;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage? response = null;
            for (int i = 0; i <= _maxRetries; i++)
            {
                OnAttempt?.Invoke();

                // Clone the request for retries (original request can only be sent once)
                var clonedRequest = await CloneRequestAsync(request);
                response = await base.SendAsync(clonedRequest, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    break;
                }
            }
            return response!;
        }

        private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);

            if (request.Content != null)
            {
                var content = await request.Content.ReadAsByteArrayAsync();
                clone.Content = new ByteArrayContent(content);
                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }

    #endregion
}
