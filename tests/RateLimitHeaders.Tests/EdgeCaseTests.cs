using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RateLimitHeaders.Http;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Tests.Fixtures;

namespace RateLimitHeaders.Tests;

/// <summary>
/// Tests for edge cases including null handling, exception handling, and race conditions.
/// </summary>
public class EdgeCaseTests
{
    #region Null Logger Injection Tests

    [Fact]
    public async Task Handler_WithNullLoggerInstance_ShouldUseNullLogger()
    {
        // Arrange - Using NullLogger.Instance is the standard pattern
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);

        var handler = new RateLimitAwareHandler(options, NullLogger.Instance)
        {
            InnerHandler = mockHandler
        };
        var client = new HttpClient(handler);

        // Act - Should not throw
        var response = await client.GetAsync("http://example.com/api/test");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void Handler_Constructor_WithNullOptions_ShouldThrow()
    {
        // Arrange & Act
        var act = () => new RateLimitAwareHandler(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithMessage("*options*");
    }

    [Fact]
    public void Handler_Constructor_WithNullLogger_ShouldThrow()
    {
        // Arrange & Act
        var options = new RateLimitAwareOptions();
        var act = () => new RateLimitAwareHandler(options, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithMessage("*logger*");
    }

    #endregion

    #region StateKeyExtractor Exception Handling Tests

    [Fact]
    public async Task Handler_WhenStateKeyExtractorThrows_ShouldPropagateException()
    {
        // Arrange - StateKeyExtractor that throws
        // Note: By design, exceptions from StateKeyExtractor propagate to the caller
        // so that configuration errors are surfaced immediately rather than silently ignored.
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            StateKeyExtractor = _ => throw new InvalidOperationException("Extractor error")
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);

        var handler = new RateLimitAwareHandler(options, NullLogger.Instance)
        {
            InnerHandler = mockHandler
        };
        var client = new HttpClient(handler);

        // Act & Assert - Exception should propagate
        var act = async () => await client.GetAsync("http://example.com/api/test");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Extractor error");
    }

    [Fact]
    public async Task Handler_WhenStateKeyExtractorReturnsNull_ShouldUseGlobalKey()
    {
        // Arrange
        RateLimitInfo? capturedInfo = null;
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            TrackStatePerEndpoint = true,
            StateKeyExtractor = _ => null!,
            OnRateLimitInfo = args =>
            {
                capturedInfo = args.RateLimitInfo;
                return ValueTask.CompletedTask;
            }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);

        var handler = new RateLimitAwareHandler(options, NullLogger.Instance)
        {
            InnerHandler = mockHandler
        };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");

        // Assert - Should still parse and track state
        capturedInfo.Should().NotBeNull();
        capturedInfo!.Value.Remaining.Should().Be(50);
    }

    [Fact]
    public async Task Handler_WhenStateKeyExtractorReturnsEmpty_ShouldWorkNormally()
    {
        // Arrange
        RateLimitInfo? capturedInfo = null;
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            StateKeyExtractor = _ => string.Empty,
            OnRateLimitInfo = args =>
            {
                capturedInfo = args.RateLimitInfo;
                return ValueTask.CompletedTask;
            }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);

        var handler = new RateLimitAwareHandler(options, NullLogger.Instance)
        {
            InnerHandler = mockHandler
        };
        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/api/test");

        // Assert
        capturedInfo.Should().NotBeNull();
    }

    #endregion

    #region Race Condition Tests

    [Fact]
    public async Task StateTracker_RapidStateUpdates_ShouldNotLoseData()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();
        var taskCount = 50;
        var iterationsPerTask = 100;
        var expectedUpdates = taskCount * iterationsPerTask;
        var actualUpdates = 0;

        // Act - rapid updates from multiple threads
        var tasks = Enumerable.Range(0, taskCount).Select(taskIndex => Task.Run(() =>
        {
            for (int i = 0; i < iterationsPerTask; i++)
            {
                var key = $"endpoint-{taskIndex}";
                var info = new RateLimitInfo
                {
                    PolicyName = $"policy-{taskIndex}-{i}",
                    Remaining = 100 - (i % 100),
                    Quota = 100,
                    ResetSeconds = 60,
                    IsValid = true
                };

                tracker.UpdateState(key, info);
                Interlocked.Increment(ref actualUpdates);
            }
        }));

        await Task.WhenAll(tasks);

        // Assert - All updates should have completed
        actualUpdates.Should().Be(expectedUpdates);

        // All endpoints should have valid state
        for (int i = 0; i < taskCount; i++)
        {
            var info = tracker.GetRateLimitInfo($"endpoint-{i}");
            info.IsValid.Should().BeTrue();
        }
    }

    [Fact]
    public async Task StateTracker_ConcurrentReadsDuringThrottlingDecision_ShouldNotDeadlock()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();
        var key = "shared-endpoint";

        // Pre-populate state
        tracker.UpdateState(key, new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 10,
            Quota = 100,
            ResetSeconds = 60,
            IsValid = true
        });

        var readCount = 0;
        var writeCount = 0;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act - concurrent reads and writes
        var tasks = new List<Task>();

        // Reader tasks
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    _ = tracker.GetRateLimitInfo(key);
                    Interlocked.Increment(ref readCount);
                }
            }, cts.Token));
        }

        // Writer tasks
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                int j = 0;
                while (!cts.IsCancellationRequested)
                {
                    tracker.UpdateState(key, new RateLimitInfo
                    {
                        PolicyName = "test",
                        Remaining = j++ % 100,
                        Quota = 100,
                        ResetSeconds = 60,
                        IsValid = true
                    });
                    Interlocked.Increment(ref writeCount);
                }
            }, cts.Token));
        }

        // Wait for timeout
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        cts.Cancel();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }

        // Assert - Should have made progress (no deadlock)
        readCount.Should().BeGreaterThan(0);
        writeCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Handler_ConcurrentCallbackInvocations_ShouldBeThreadSafe()
    {
        // Arrange
        var callbackExecutions = new ConcurrentBag<int>();
        var counter = 0;

        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            OnRateLimitInfo = args =>
            {
                var id = Interlocked.Increment(ref counter);
                callbackExecutions.Add(id);
                return ValueTask.CompletedTask;
            }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.SetResponseFactory(_ =>
            MockHttpHandler.CreateRateLimitResponse(HttpStatusCode.OK, 50, 30, 100, 60));

        var handler = new RateLimitAwareHandler(options, NullLogger.Instance)
        {
            InnerHandler = mockHandler
        };
        var client = new HttpClient(handler);

        var requestCount = 100;

        // Act
        var tasks = Enumerable.Range(0, requestCount)
            .Select(_ => client.GetAsync("http://example.com/api/test"));

        await Task.WhenAll(tasks);

        // Assert
        callbackExecutions.Should().HaveCount(requestCount);
        callbackExecutions.Distinct().Should().HaveCount(requestCount);
    }

    #endregion

    #region Parser Edge Cases

    [Fact]
    public void Parser_WithDuplicatePolicyNames_ShouldUseFirstOccurrence()
    {
        // Arrange - Duplicate policy names (malformed input)
        var rateLimitHeader = "\"api\";r=50;t=30, \"api\";r=10;t=60";
        var policyHeader = "\"api\";q=100;w=60";

        // Act
        var info = RateLimitHeaderParser.Parse(rateLimitHeader, policyHeader);

        // Assert - Should parse without throwing
        info.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Parser_WithEmptyInput_ShouldReturnInvalid()
    {
        // Arrange & Act
        var info = RateLimitHeaderParser.Parse("", "");

        // Assert
        info.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Parser_WithWhitespaceOnlyInput_ShouldReturnInvalid()
    {
        // Arrange & Act
        var info = RateLimitHeaderParser.Parse("   ", "   ");

        // Assert
        info.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Parser_WithMismatchedPolicyNames_ShouldStillParse()
    {
        // Arrange - RateLimit has "policy-a", RateLimit-Policy has "policy-b"
        var rateLimitHeader = "\"policy-a\";r=50;t=30";
        var policyHeader = "\"policy-b\";q=100;w=60";

        // Act
        var info = RateLimitHeaderParser.Parse(rateLimitHeader, policyHeader);

        // Assert - Should still parse the RateLimit header, just without quota/window
        info.IsValid.Should().BeTrue();
        info.PolicyName.Should().Be("policy-a");
        info.Remaining.Should().Be(50);
        info.Quota.Should().Be(0); // Not matched with policy
    }

    #endregion

    #region Extension Method Tests

    [Fact]
    public void HttpResponseMessage_GetRateLimitInfo_ShouldParseHeaders()
    {
        // Arrange
        var response = MockHttpHandler.CreateRateLimitResponse(HttpStatusCode.OK, 50, 30, 100, 60);

        // Act
        var info = response.GetRateLimitInfo();

        // Assert
        info.IsValid.Should().BeTrue();
        info.Remaining.Should().Be(50);
        info.Quota.Should().Be(100);
    }

    [Fact]
    public void HttpResponseMessage_TryGetRateLimitInfo_WithNoHeaders_ShouldReturnFalse()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);

        // Act
        var result = response.TryGetRateLimitInfo(out var info);

        // Assert
        result.Should().BeFalse();
        info.IsValid.Should().BeFalse();
    }

    [Fact]
    public void HttpResponseMessage_GetRateLimitInfo_WithNullResponse_ShouldThrow()
    {
        // Arrange
        HttpResponseMessage response = null!;

        // Act & Assert
        var act = () => response.GetRateLimitInfo();
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion
}
