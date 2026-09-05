using Microsoft.Extensions.Time.Testing;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;

namespace RateLimitHeaders.Tests;

/// <summary>
/// Unit tests for RateLimitStateTracker.
/// </summary>
public class RateLimitStateTrackerTests
{
    [Fact]
    public void UpdateState_ShouldStoreInfo()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();
        var info = new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 50,
            Quota = 100,
            ResetSeconds = 60,
            IsValid = true
        };

        // Act
        tracker.UpdateState("key1", info);
        var retrieved = tracker.GetRateLimitInfo("key1");

        // Assert
        retrieved.IsValid.Should().BeTrue();
        retrieved.Remaining.Should().Be(50);
        retrieved.PolicyName.Should().Be("test");
    }

    [Fact]
    public void GetRateLimitInfo_WithNonExistentKey_ShouldReturnInvalid()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();

        // Act
        var result = tracker.GetRateLimitInfo("nonexistent");

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void UpdateState_ShouldOverwriteExisting()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();

        // Act
        tracker.UpdateState("key1", new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 100,
            Quota = 100,
            IsValid = true
        });

        tracker.UpdateState("key1", new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 50,
            Quota = 100,
            IsValid = true
        });

        var result = tracker.GetRateLimitInfo("key1");

        // Assert
        result.Remaining.Should().Be(50);
    }

    [Fact]
    public void Remove_ShouldRemoveEntry()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();
        tracker.UpdateState("key1", new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 50,
            Quota = 100,
            IsValid = true
        });

        // Act
        var removed = tracker.Remove("key1");
        var result = tracker.GetRateLimitInfo("key1");

        // Assert
        removed.Should().BeTrue();
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Remove_WithNonExistentKey_ShouldReturnFalse()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();

        // Act
        var removed = tracker.Remove("nonexistent");

        // Assert
        removed.Should().BeFalse();
    }

    [Fact]
    public void Clear_ShouldRemoveAllEntries()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();
        for (int i = 0; i < 10; i++)
        {
            tracker.UpdateState($"key-{i}", new RateLimitInfo
            {
                PolicyName = "test",
                Remaining = 50,
                Quota = 100,
                IsValid = true
            });
        }

        // Act
        tracker.Clear();

        // Assert
        for (int i = 0; i < 10; i++)
        {
            tracker.GetRateLimitInfo($"key-{i}").IsValid.Should().BeFalse();
        }
    }

    [Fact]
    public async Task RemoveStaleEntries_ShouldRemoveOldEntries()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();

        // Add entry that will become stale (not updated after this)
        tracker.UpdateState("stale", new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 50,
            Quota = 100,
            ResetSeconds = 60,
            IsValid = true
        });

        // Wait to make the "stale" entry old
        await Task.Delay(1500);

        // Add a fresh entry (updated after the delay)
        tracker.UpdateState("fresh", new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 50,
            Quota = 100,
            ResetSeconds = 60,
            IsValid = true
        });

        // Act - remove entries older than 1 second (stale was added 1.5s ago, fresh just now)
        var removed = tracker.RemoveStaleEntries(TimeSpan.FromSeconds(1));

        // Assert
        removed.Should().Be(1);
        tracker.GetRateLimitInfo("stale").IsValid.Should().BeFalse();
        tracker.GetRateLimitInfo("fresh").IsValid.Should().BeTrue();
    }

    [Fact]
    public void RemoveStaleEntries_WithNoStaleEntries_ShouldReturnZero()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();
        tracker.UpdateState("fresh", new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 50,
            Quota = 100,
            ResetSeconds = 3600,
            IsValid = true
        });

        // Act
        var removed = tracker.RemoveStaleEntries(TimeSpan.FromSeconds(1));

        // Assert
        removed.Should().Be(0);
        tracker.GetRateLimitInfo("fresh").IsValid.Should().BeTrue();
    }

    [Fact]
    public void GetRateLimitInfo_WithNullKey_ShouldThrow()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();

        // Act
        var act = () => tracker.GetRateLimitInfo(null!);

        // Assert - ConcurrentDictionary throws on null key
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void UpdateState_WithNullKey_ShouldThrow()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();

        // Act
        var act = () => tracker.UpdateState(null!, new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 50,
            IsValid = true
        });

        // Assert - ConcurrentDictionary throws on null key
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void UpdateState_MultipleKeys_ShouldTrackIndependently()
    {
        // Arrange
        var tracker = new RateLimitStateTracker();

        // Act
        tracker.UpdateState("api-v1", new RateLimitInfo
        {
            PolicyName = "api-v1",
            Remaining = 100,
            Quota = 100,
            IsValid = true
        });

        tracker.UpdateState("api-v2", new RateLimitInfo
        {
            PolicyName = "api-v2",
            Remaining = 50,
            Quota = 200,
            IsValid = true
        });

        // Assert
        tracker.GetRateLimitInfo("api-v1").Remaining.Should().Be(100);
        tracker.GetRateLimitInfo("api-v1").Quota.Should().Be(100);

        tracker.GetRateLimitInfo("api-v2").Remaining.Should().Be(50);
        tracker.GetRateLimitInfo("api-v2").Quota.Should().Be(200);
    }

    [Fact]
    public void GetRateLimitInfo_AfterExpiry_ReadsInvalidButRawSnapshotStaysStored()
    {
        // Arrange: a 1-second window observed at a fixed instant.
        // The audit (AUD-03 in TRACKER-adversarial-audit.md) condemned the old behavior of
        // returning the raw snapshot after the window elapsed; the adjusted read must now be
        // invalid, while the raw snapshot stays reachable via GetState until cleanup removes it.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var tracker = new RateLimitStateTracker(clock);
        tracker.UpdateState("key", new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 0,
            Quota = 100,
            ResetSeconds = 1,
            IsValid = true
        });

        // Advance past the reset moment
        clock.Advance(TimeSpan.FromSeconds(1.5));

        // Act - entry is stale but not yet cleaned up
        var adjusted = tracker.GetRateLimitInfo("key");
        var raw = tracker.GetState("key");

        // Assert
        adjusted.IsValid.Should().BeFalse("an elapsed window must no longer drive decisions");
        raw.Should().NotBeNull("cleanup has not run, so the raw snapshot is still stored");
        raw!.Value.Info.Remaining.Should().Be(0);
    }

    [Fact]
    public async Task AutomaticCleanup_ShouldRemoveStaleEntriesAfterThreshold()
    {
        // Arrange: staleness is measured on a fake clock so no real sleep decides it
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var tracker = new RateLimitStateTracker(clock)
        {
            CleanupFrequency = 5,  // Cleanup every 5 updates
            StaleEntryMaxAge = TimeSpan.FromMilliseconds(500)  // Entries older than 500ms are stale
        };

        // Add initial entry that will become stale
        tracker.UpdateState("stale-key", new RateLimitInfo
        {
            PolicyName = "test",
            Remaining = 50,
            IsValid = true
        });

        // Make the entry stale
        clock.Advance(TimeSpan.FromMilliseconds(600));

        // Act - trigger automatic cleanup by making 5 updates
        for (int i = 0; i < 5; i++)
        {
            tracker.UpdateState($"fresh-key-{i}", new RateLimitInfo
            {
                PolicyName = "test",
                Remaining = 50,
                IsValid = true
            });
        }

        // The cleanup runs on a background thread; poll until it removes the entry
        // instead of racing it with a fixed sleep (this test failed on a slow CI runner)
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (tracker.GetState("stale-key") is not null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        // Assert - stale entry should be removed
        tracker.GetState("stale-key").Should().BeNull();

        // Fresh entries should still exist
        for (int i = 0; i < 5; i++)
        {
            tracker.GetRateLimitInfo($"fresh-key-{i}").IsValid.Should().BeTrue();
        }
    }

    [Fact]
    public void CleanupFrequency_DefaultValue_ShouldBe100()
    {
        // Arrange & Act
        var tracker = new RateLimitStateTracker();

        // Assert
        tracker.CleanupFrequency.Should().Be(100);
    }

    [Fact]
    public void StaleEntryMaxAge_DefaultValue_ShouldBeOneHour()
    {
        // Arrange & Act
        var tracker = new RateLimitStateTracker();

        // Assert
        tracker.StaleEntryMaxAge.Should().Be(TimeSpan.FromHours(1));
    }
}
