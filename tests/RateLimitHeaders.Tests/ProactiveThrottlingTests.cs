using System.Diagnostics;
using RateLimitHeaders.Events;
using RateLimitHeaders.Http;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Tests.Fixtures;
using RateLimitHeaders.Throttling;

namespace RateLimitHeaders.Tests;

/// <summary>
/// Tests for proactive throttling behavior.
/// </summary>
public class ProactiveThrottlingTests
{
    [Fact]
    public async Task Handler_WithThrottlingEnabled_ShouldDelayRequests()
    {
        // Arrange
        var stateTracker = new RateLimitStateTracker();

        // Pre-populate with low quota to trigger throttling
        stateTracker.UpdateState("global", new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 5,   // 5% remaining
            Quota = 100,
            ResetSeconds = 60,
            IsValid = true
        });

        var throttleCallbackInvoked = false;
        TimeSpan? delayReceived = null;

        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = true,
            TrackStatePerEndpoint = false,  // Use "global" key for testing
            OnThrottling = args =>
            {
                throttleCallbackInvoked = true;
                delayReceived = args.Delay;
                return ValueTask.CompletedTask;
            }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(5, 60, 100, 60);

        var handler = new RateLimitAwareHandler(options, stateTracker, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            InnerHandler = mockHandler
        };
        var client = new HttpClient(handler);

        var sw = Stopwatch.StartNew();

        // Act
        await client.GetAsync("http://example.com/api/test");
        sw.Stop();

        // Assert
        throttleCallbackInvoked.Should().BeTrue("throttling callback should be invoked when quota is low");
        delayReceived.Should().NotBeNull();
        delayReceived!.Value.Should().BeGreaterThan(TimeSpan.Zero);
        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(100), "request should have been delayed");
    }

    [Fact]
    public async Task Handler_WithThrottlingDisabled_ShouldNotDelay()
    {
        // Arrange
        var stateTracker = new RateLimitStateTracker();
        stateTracker.UpdateState("global", new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 1,  // Very low
            Quota = 100,
            ResetSeconds = 60,
            IsValid = true
        });

        var throttleCallbackInvoked = false;
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,  // Disabled
            TrackStatePerEndpoint = false,  // Use "global" key for testing
            OnThrottling = _ => { throttleCallbackInvoked = true; return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(1, 60, 100, 60);

        var handler = new RateLimitAwareHandler(options, stateTracker, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            InnerHandler = mockHandler
        };
        var client = new HttpClient(handler);

        var sw = Stopwatch.StartNew();

        // Act
        await client.GetAsync("http://example.com/api/test");
        sw.Stop();

        // Assert
        throttleCallbackInvoked.Should().BeFalse();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "no delay should have been applied");
    }

    [Fact]
    public async Task Handler_WithQuotaAboveThreshold_ShouldNotDelay()
    {
        // Arrange
        var stateTracker = new RateLimitStateTracker();
        stateTracker.UpdateState("global", new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 50,  // 50% - well above default 10% threshold
            Quota = 100,
            ResetSeconds = 60,
            IsValid = true
        });

        var throttleCallbackInvoked = false;
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = true,
            TrackStatePerEndpoint = false,  // Use "global" key for testing
            OnThrottling = _ => { throttleCallbackInvoked = true; return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 60, 100, 60);

        var handler = new RateLimitAwareHandler(options, stateTracker, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            InnerHandler = mockHandler
        };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");

        // Assert
        throttleCallbackInvoked.Should().BeFalse("quota is above threshold, no throttling needed");
    }

    [Fact]
    public async Task Handler_ThrottlingDelayRespectsCancellation()
    {
        // Arrange
        var stateTracker = new RateLimitStateTracker();
        stateTracker.UpdateState("global", new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 1,  // Very low - will trigger long delay
            Quota = 100,
            ResetSeconds = 60,
            IsValid = true
        });

        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = true,
            TrackStatePerEndpoint = false,  // Use "global" key for testing
            ThrottlingAlgorithm = new PercentageThrottlingAlgorithm(
                threshold: 0.5,  // High threshold to ensure throttling
                factor: 1.0,
                maxDelay: TimeSpan.FromSeconds(30))  // Long max delay
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(1, 60, 100, 60);

        var handler = new RateLimitAwareHandler(options, stateTracker, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            InnerHandler = mockHandler
        };
        var client = new HttpClient(handler);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        // Act & Assert
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.GetAsync("http://example.com/api/test", cts.Token));
    }

    [Fact]
    public async Task Handler_CustomThrottlingAlgorithm_ShouldBeUsed()
    {
        // Arrange
        var customAlgorithmCalled = false;
        var customAlgorithm = new TestThrottlingAlgorithm(() =>
        {
            customAlgorithmCalled = true;
            return ThrottlingResult.NoThrottle;
        });

        var stateTracker = new RateLimitStateTracker();
        // Use "global" key since we'll disable TrackStatePerEndpoint
        stateTracker.UpdateState("global", new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 5,
            Quota = 100,
            ResetSeconds = 60,
            IsValid = true
        });

        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = true,
            TrackStatePerEndpoint = false,  // Use "global" key
            ThrottlingAlgorithm = customAlgorithm
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(5, 60, 100, 60);

        var handler = new RateLimitAwareHandler(options, stateTracker, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            InnerHandler = mockHandler
        };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");

        // Assert
        customAlgorithmCalled.Should().BeTrue("custom algorithm should be invoked");
    }

    [Fact]
    public async Task Handler_ThrottlingEventArgs_ContainsCorrectInfo()
    {
        // Arrange
        var stateTracker = new RateLimitStateTracker();
        stateTracker.UpdateState("global", new RateLimitInfo
        {
            PolicyName = "test-policy",
            Remaining = 3,  // 3% remaining
            Quota = 100,
            ResetSeconds = 45,
            IsValid = true
        });

        ThrottlingEventArgs? receivedArgs = null;
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = true,
            TrackStatePerEndpoint = false,  // Use "global" key for testing
            OnThrottling = args => { receivedArgs = args; return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(3, 45, 100, 60);

        var handler = new RateLimitAwareHandler(options, stateTracker, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            InnerHandler = mockHandler
        };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Value.RateLimitInfo.PolicyName.Should().Be("test-policy");
        receivedArgs.Value.RateLimitInfo.Remaining.Should().Be(3);
        receivedArgs.Value.RequestUri.Should().NotBeNull();
        receivedArgs.Value.Delay.Should().BeGreaterThan(TimeSpan.Zero);
        receivedArgs.Value.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Handler_FirstRequest_ShouldNotThrottle()
    {
        // Arrange - empty state tracker (no prior requests)
        var throttleCallbackInvoked = false;
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = true,
            OnThrottling = _ => { throttleCallbackInvoked = true; return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");

        // Assert - first request has no prior state, so no throttling
        throttleCallbackInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task Handler_MultipleRequests_ShouldThrottleWhenQuotaDrops()
    {
        // Arrange
        var throttleCallCount = 0;
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = true,
            OnThrottling = _ => { throttleCallCount++; return ValueTask.CompletedTask; }
        };

        var mockHandler = new MockHttpHandler();
        // First few requests have good quota
        mockHandler.QueueRateLimitResponse(90, 60, 100, 60);
        mockHandler.QueueRateLimitResponse(80, 60, 100, 60);
        mockHandler.QueueRateLimitResponse(70, 60, 100, 60);
        // Then quota drops below threshold
        mockHandler.QueueRateLimitResponse(5, 60, 100, 60);
        mockHandler.QueueRateLimitResponse(4, 60, 100, 60);

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");  // 1st - no throttle (no prior state)
        await client.GetAsync("http://example.com/api/test");  // 2nd - no throttle (90% remaining)
        await client.GetAsync("http://example.com/api/test");  // 3rd - no throttle (80% remaining)
        await client.GetAsync("http://example.com/api/test");  // 4th - no throttle (70% remaining)
        await client.GetAsync("http://example.com/api/test");  // 5th - THROTTLE (5% remaining from 4th response)

        // Assert - throttling should occur on 5th request (based on 4th response showing 5%)
        throttleCallCount.Should().Be(1);
    }

    private sealed class TestThrottlingAlgorithm : IThrottlingAlgorithm
    {
        private readonly Func<ThrottlingResult> _evaluator;

        public TestThrottlingAlgorithm(Func<ThrottlingResult> evaluator)
        {
            _evaluator = evaluator;
        }

        public ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo) => _evaluator();
    }
}
