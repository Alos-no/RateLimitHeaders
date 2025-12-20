using System.Collections.Concurrent;
using System.Net;
using RateLimitHeaders.Http;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Tests.Fixtures;

namespace RateLimitHeaders.Tests;

/// <summary>
/// Tests for thread-safety and concurrent access patterns.
/// </summary>
public class ConcurrencyTests
{
    [Fact]
    public async Task StateTracker_ConcurrentUpdates_ShouldNotCorruptState()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();
        var taskCount = 100;
        var iterationsPerTask = 100;
        var exceptions = new ConcurrentBag<Exception>();

        // Act - hammer the tracker from multiple threads
        var tasks = Enumerable.Range(0, taskCount).Select(taskIndex => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < iterationsPerTask; i++)
                {
                    var key = $"endpoint-{taskIndex % 10}";  // 10 different endpoints
                    var info = new RateLimitInfo
                    {
                        PolicyName = "test",
                        Remaining = 100 - (i % 100),
                        Quota = 100,
                        ResetSeconds = 60,
                        IsValid = true
                    };

                    tracker.UpdateState(key, info);

                    // Also do reads
                    var _ = tracker.GetRateLimitInfo(key);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        await Task.WhenAll(tasks);

        // Assert
        exceptions.Should().BeEmpty("No exceptions should occur during concurrent access");
    }

    [Fact]
    public async Task StateTracker_ConcurrentRemoveStaleEntries_ShouldNotThrow()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();

        // Pre-populate with some entries
        for (int i = 0; i < 50; i++)
        {
            tracker.UpdateState($"key-{i}", new RateLimitInfo
            {
                PolicyName = "test",
                Remaining = 50,
                Quota = 100,
                ResetSeconds = 1,  // Very short so they become stale
                IsValid = true
            });
        }

        // Wait for entries to become stale
        await Task.Delay(1500);

        var exceptions = new ConcurrentBag<Exception>();

        // Act - concurrent cleanup and updates
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            try
            {
                if (i % 2 == 0)
                {
                    tracker.RemoveStaleEntries(TimeSpan.FromSeconds(5));
                }
                else
                {
                    tracker.UpdateState($"new-key-{i}", new RateLimitInfo
                    {
                        PolicyName = "test",
                        Remaining = 50,
                        Quota = 100,
                        ResetSeconds = 60,
                        IsValid = true
                    });
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        await Task.WhenAll(tasks);

        // Assert
        exceptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Handler_ConcurrentRequests_ShouldAllReceiveCallbacks()
    {
        // Arrange
        var receivedCount = 0;
        var lockObj = new object();

        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            OnRateLimitInfo = _ =>
            {
                lock (lockObj)
                {
                    receivedCount++;
                }
                return ValueTask.CompletedTask;
            }
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.SetResponseFactory(_ => MockHttpHandler.CreateRateLimitResponse(
            HttpStatusCode.OK,
            remaining: 50,
            resetSeconds: 30,
            quota: 100,
            windowSeconds: 60));

        var handler = new RateLimitAwareHandler(options) { InnerHandler = mockHandler };
        var client = new HttpClient(handler);

        var requestCount = 50;

        // Act - make concurrent requests
        var tasks = Enumerable.Range(0, requestCount)
            .Select(_ => client.GetAsync("http://example.com/api/test"));

        await Task.WhenAll(tasks);

        // Assert
        receivedCount.Should().Be(requestCount);
    }

    [Fact]
    public async Task Handler_ConcurrentRequestsWithThrottling_ShouldNotDeadlock()
    {
        // Arrange
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = true,
            TrackStatePerEndpoint = false  // Use "global" key for testing
        };

        // Pre-populate state to trigger throttling
        var stateTracker = new RateLimitStateTracker();
        stateTracker.UpdateState("global", new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 1,  // Very low to trigger throttling
            Quota = 100,
            ResetSeconds = 60,
            IsValid = true
        });

        var mockHandler = new MockHttpHandler();
        mockHandler.SetResponseFactory(_ => MockHttpHandler.CreateRateLimitResponse(
            HttpStatusCode.OK,
            remaining: 50,
            resetSeconds: 30,
            quota: 100,
            windowSeconds: 60));

        var handler = new RateLimitAwareHandler(options, stateTracker, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            InnerHandler = mockHandler
        };
        var client = new HttpClient(handler);

        var requestCount = 10;

        // Act - make concurrent requests with short timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            var tasks = Enumerable.Range(0, requestCount)
                .Select(_ => client.GetAsync("http://example.com/api/test", cts.Token));

            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Assert.Fail("Concurrent requests with throttling caused a deadlock or timeout");
        }

        // Assert - if we got here, no deadlock occurred
    }

    [Fact]
    public void StateTracker_Clear_ShouldBeThreadSafe()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();
        var exceptions = new ConcurrentBag<Exception>();

        // Pre-populate
        for (int i = 0; i < 100; i++)
        {
            tracker.UpdateState($"key-{i}", new RateLimitInfo
            {
                PolicyName = "test",
                Remaining = 50,
                Quota = 100,
                IsValid = true
            });
        }

        // Act - concurrent clear and updates
        Parallel.For(0, 50, i =>
        {
            try
            {
                if (i == 25)
                {
                    tracker.Clear();
                }
                else
                {
                    tracker.UpdateState($"key-{i}", new RateLimitInfo
                    {
                        PolicyName = "test",
                        Remaining = 50,
                        Quota = 100,
                        IsValid = true
                    });
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Assert
        exceptions.Should().BeEmpty();
    }
}
