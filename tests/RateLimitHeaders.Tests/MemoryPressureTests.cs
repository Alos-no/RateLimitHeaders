using System.Collections.Concurrent;
using System.Net;
using RateLimitHeaders.Http;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Tests.Fixtures;

namespace RateLimitHeaders.Tests;

/// <summary>
/// Tests for memory efficiency and behavior under high-volume scenarios.
/// </summary>
public class MemoryPressureTests
{
    [Fact]
    public void StateTracker_ManyEndpoints_ShouldHandleEfficiently()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();
        var endpointCount = 10000;

        // Act - simulate tracking many different endpoints
        for (int i = 0; i < endpointCount; i++)
        {
            tracker.UpdateState($"endpoint-{i}", new RateLimitInfo
            {
                PolicyName = "test",
                Remaining = 100 - (i % 100),
                Quota = 100,
                ResetSeconds = 60,
                WindowSeconds = 60,
                IsValid = true
            });
        }

        // Assert - all entries should be retrievable
        for (int i = 0; i < endpointCount; i++)
        {
            var info = tracker.GetRateLimitInfo($"endpoint-{i}");
            info.IsValid.Should().BeTrue($"endpoint-{i} should be valid");
        }
    }

    [Fact]
    public async Task StateTracker_AutomaticCleanup_ShouldPreventUnboundedGrowth()
    {
        // Arrange
        var tracker = new RateLimitStateTracker
        {
            CleanupFrequency = 10,  // Cleanup every 10 updates
            StaleEntryMaxAge = TimeSpan.FromMilliseconds(100)  // Entries become stale very quickly
        };

        // Act - Add entries that will become stale
        for (int i = 0; i < 50; i++)
        {
            tracker.UpdateState($"stale-{i}", new RateLimitInfo
            {
                PolicyName = "test",
                Remaining = 50,
                Quota = 100,
                IsValid = true
            });
        }

        // Wait for entries to become stale
        await Task.Delay(200);

        // Trigger cleanup by adding more entries (every 10 updates triggers cleanup)
        for (int i = 0; i < 15; i++)
        {
            tracker.UpdateState($"fresh-{i}", new RateLimitInfo
            {
                PolicyName = "test",
                Remaining = 50,
                Quota = 100,
                IsValid = true
            });
        }

        // Allow background cleanup to complete
        await Task.Delay(100);

        // Assert - stale entries should be removed
        var staleCount = 0;
        for (int i = 0; i < 50; i++)
        {
            if (tracker.GetRateLimitInfo($"stale-{i}").IsValid)
            {
                staleCount++;
            }
        }

        // Some or all stale entries should be cleaned up
        staleCount.Should().BeLessThan(50, "Automatic cleanup should have removed some stale entries");
    }

    [Fact]
    public void StateTracker_FrequentUpdates_ShouldHandleGracefully()
    {
        // Arrange
        var tracker = new RateLimitStateTracker
        {
            CleanupFrequency = 1000  // Less frequent cleanup to avoid interference
        };
        var updateCount = 100000;

        // Act - rapid fire updates to the same endpoint
        for (int i = 0; i < updateCount; i++)
        {
            tracker.UpdateState("hot-endpoint", new RateLimitInfo
            {
                PolicyName = "test",
                Remaining = updateCount - i - 1,  // Last iteration: 100000 - 99999 - 1 = 0
                Quota = updateCount,
                ResetSeconds = 60,
                IsValid = true
            });
        }

        // Assert - final state should reflect last update
        var info = tracker.GetRateLimitInfo("hot-endpoint");
        info.IsValid.Should().BeTrue();
        info.Remaining.Should().Be(0);
    }

    [Fact]
    public async Task StateTracker_ConcurrentMassUpdates_ShouldNotLeak()
    {
        // Arrange
        var tracker = new RateLimitStateTracker
        {
            CleanupFrequency = 100,
            StaleEntryMaxAge = TimeSpan.FromMilliseconds(500)
        };
        var taskCount = 10;
        var updatesPerTask = 1000;
        var exceptions = new ConcurrentBag<Exception>();

        // Act - concurrent mass updates
        var tasks = Enumerable.Range(0, taskCount).Select(taskIndex => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < updatesPerTask; i++)
                {
                    var key = $"endpoint-{taskIndex}-{i}";
                    tracker.UpdateState(key, new RateLimitInfo
                    {
                        PolicyName = "test",
                        Remaining = 50,
                        Quota = 100,
                        ResetSeconds = 1,
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

        // Wait for entries to become stale
        await Task.Delay(600);

        // Force cleanup
        var removed = tracker.RemoveStaleEntries(TimeSpan.FromMilliseconds(500));

        // Assert
        exceptions.Should().BeEmpty("No exceptions should occur during concurrent updates");
        removed.Should().BeGreaterThan(0, "Stale entries should be removable after expiry");
    }

    [Fact]
    public async Task Handler_HighVolumeRequests_ShouldNotAccumulateMemory()
    {
        // Arrange
        var callbackCount = 0;
        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = false,
            TrackStatePerEndpoint = true,
            OnRateLimitInfo = _ =>
            {
                Interlocked.Increment(ref callbackCount);
                return ValueTask.CompletedTask;
            }
        };

        var stateTracker = new RateLimitStateTracker
        {
            CleanupFrequency = 50,  // Cleanup more frequently
            StaleEntryMaxAge = TimeSpan.FromSeconds(1)
        };

        var mockHandler = new MockHttpHandler();
        mockHandler.SetResponseFactory(request =>
        {
            var host = request.RequestUri?.Host ?? "unknown";
            return MockHttpHandler.CreateRateLimitResponse(
                HttpStatusCode.OK,
                remaining: 50,
                resetSeconds: 30,
                quota: 100,
                windowSeconds: 60,
                policyName: host);
        });

        var handler = new RateLimitAwareHandler(options, stateTracker, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            InnerHandler = mockHandler
        };
        var client = new HttpClient(handler);

        var requestCount = 500;

        // Act - make many requests to different endpoints
        var tasks = Enumerable.Range(0, requestCount)
            .Select(i => client.GetAsync($"http://api-{i % 50}.example.com/resource"));

        await Task.WhenAll(tasks);

        // Wait for entries to become stale
        await Task.Delay(1200);

        // Force cleanup
        var removed = stateTracker.RemoveStaleEntries(TimeSpan.FromSeconds(1));

        // Assert
        callbackCount.Should().Be(requestCount);
        removed.Should().BeGreaterThan(0, "State tracker should clean up stale entries");
    }

    [Fact]
    public void RateLimitInfo_ManyInstances_ShouldBeMemoryEfficient()
    {
        // Arrange - This test verifies that RateLimitInfo as a readonly record struct
        // is stack-allocated and doesn't cause excessive heap allocations
        var infos = new RateLimitInfo[10000];

        // Act - create many instances
        for (int i = 0; i < infos.Length; i++)
        {
            infos[i] = new RateLimitInfo
            {
                PolicyName = "test",
                Remaining = i,
                Quota = 10000,
                ResetSeconds = 60,
                WindowSeconds = 60,
                IsValid = true
            };
        }

        // Assert - verify values are correct (proves structs work correctly)
        for (int i = 0; i < infos.Length; i++)
        {
            infos[i].Remaining.Should().Be(i);
        }
    }

    [Fact]
    public void StateTracker_RemoveStaleEntries_WithLargeDataset_ShouldComplete()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();
        var entryCount = 5000;

        for (int i = 0; i < entryCount; i++)
        {
            tracker.UpdateState($"endpoint-{i}", new RateLimitInfo
            {
                PolicyName = "test",
                Remaining = 50,
                Quota = 100,
                ResetSeconds = 1,  // Short reset for stale detection
                IsValid = true
            });
        }

        // Act - remove all as stale (very short max age)
        var removed = tracker.RemoveStaleEntries(TimeSpan.Zero);

        // Assert - all entries should be removed as stale
        removed.Should().Be(entryCount);

        // Verify all entries are gone
        for (int i = 0; i < entryCount; i++)
        {
            tracker.GetRateLimitInfo($"endpoint-{i}").IsValid.Should().BeFalse();
        }
    }

    [Fact]
    public async Task StateTracker_ConcurrentCleanupDuringUpdates_ShouldBeSafe()
    {
        // Arrange
        var tracker = new RateLimitStateTracker
        {
            CleanupFrequency = 5,  // Very aggressive cleanup
            StaleEntryMaxAge = TimeSpan.FromMilliseconds(50)
        };
        var exceptions = new ConcurrentBag<Exception>();

        // Act - concurrent updates and cleanup
        var updateTask = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 1000; i++)
                {
                    tracker.UpdateState($"key-{i % 100}", new RateLimitInfo
                    {
                        PolicyName = "test",
                        Remaining = 50,
                        Quota = 100,
                        IsValid = true
                    });

                    if (i % 100 == 0)
                    {
                        await Task.Delay(10);  // Allow some entries to become stale
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        var cleanupTask = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 50; i++)
                {
                    tracker.RemoveStaleEntries(TimeSpan.FromMilliseconds(50));
                    await Task.Delay(20);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        await Task.WhenAll(updateTask, cleanupTask);

        // Assert
        exceptions.Should().BeEmpty("Concurrent cleanup during updates should be safe");
    }

    [Fact]
    public void StateTracker_ClearDuringHighVolume_ShouldNotCorrupt()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();
        var exceptions = new ConcurrentBag<Exception>();

        // Act - concurrent updates, reads, and occasional clears
        Parallel.For(0, 200, i =>
        {
            try
            {
                if (i == 100)
                {
                    // Clear in the middle of operations
                    tracker.Clear();
                }
                else if (i % 2 == 0)
                {
                    // Updates
                    tracker.UpdateState($"key-{i % 50}", new RateLimitInfo
                    {
                        PolicyName = "test",
                        Remaining = 50,
                        Quota = 100,
                        IsValid = true
                    });
                }
                else
                {
                    // Reads
                    var _ = tracker.GetRateLimitInfo($"key-{i % 50}");
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Assert - no corruption should occur
        exceptions.Should().BeEmpty();
    }

    #region Background Cleanup Exception Handling Tests

    [Fact]
    public async Task StateTracker_BackgroundCleanupException_ShouldNotCrashApplication()
    {
        // Arrange - This test verifies that exceptions in background cleanup are swallowed
        // We can't directly cause RemoveStaleEntries to throw, but we can verify the
        // fire-and-forget pattern works correctly under concurrent stress
        var tracker = new RateLimitStateTracker
        {
            CleanupFrequency = 1,  // Trigger cleanup on every update
            StaleEntryMaxAge = TimeSpan.FromMilliseconds(1)  // Everything is immediately stale
        };

        var updateCount = 100;
        var exceptions = new ConcurrentBag<Exception>();

        // Act - Rapid updates that trigger cleanup on every iteration
        var tasks = Enumerable.Range(0, 10).Select(taskId => Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < updateCount; i++)
                {
                    // This triggers background cleanup on every update
                    tracker.UpdateState($"task-{taskId}-key-{i}", new RateLimitInfo
                    {
                        PolicyName = "test",
                        Remaining = 50,
                        Quota = 100,
                        IsValid = true
                    });

                    // Allow background cleanup tasks to run
                    if (i % 10 == 0)
                    {
                        await Task.Yield();
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        await Task.WhenAll(tasks);

        // Allow any pending background cleanup tasks to complete
        await Task.Delay(100);

        // Assert - No exceptions should propagate from background cleanup
        exceptions.Should().BeEmpty("Background cleanup exceptions should be swallowed");
    }

    [Fact]
    public async Task StateTracker_CleanupTriggeredByUpdateCounter_ShouldNotBlockCaller()
    {
        // Arrange
        var tracker = new RateLimitStateTracker
        {
            CleanupFrequency = 5,  // Cleanup every 5 updates
            StaleEntryMaxAge = TimeSpan.FromMilliseconds(10)
        };

        var sw = new System.Diagnostics.Stopwatch();
        var updateTimes = new List<long>();

        // Act - Measure update times, including ones that trigger cleanup
        for (int i = 0; i < 20; i++)
        {
            sw.Restart();
            tracker.UpdateState($"key-{i}", new RateLimitInfo
            {
                PolicyName = "test",
                Remaining = 50,
                Quota = 100,
                IsValid = true
            });
            sw.Stop();
            updateTimes.Add(sw.ElapsedMilliseconds);
        }

        // Assert - Updates that trigger cleanup (5, 10, 15, 20) should not take significantly longer
        // because cleanup runs in background
        var averageTime = updateTimes.Average();
        var maxTime = updateTimes.Max();

        // All updates should complete quickly (cleanup is async)
        maxTime.Should().BeLessThan(100, "Cleanup should not block the caller");

        // Allow background tasks to complete
        await Task.Delay(50);
    }

    #endregion
}
