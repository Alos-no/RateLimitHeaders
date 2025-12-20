using System.Collections.Concurrent;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Throttling;

namespace RateLimitHeaders.Internal;

/// <summary>
/// Thread-safe tracker for per-endpoint rate limit state.
/// </summary>
internal sealed class RateLimitStateTracker : IRateLimitStateProvider
{
    private readonly ConcurrentDictionary<string, RateLimitState> _states = new(StringComparer.OrdinalIgnoreCase);
    private int _updateCounter;
    private int _cleanupInProgress;

    /// <summary>
    /// The frequency of automatic cleanup (every N updates).
    /// </summary>
    public int CleanupFrequency { get; set; } = 100;

    /// <summary>
    /// The maximum age for entries before they are considered stale and eligible for cleanup.
    /// Default is 1 hour.
    /// </summary>
    public TimeSpan StaleEntryMaxAge { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets the current state for a given endpoint key.
    /// </summary>
    /// <param name="key">The endpoint key.</param>
    /// <returns>The current state, or null if no state exists.</returns>
    public RateLimitState? GetState(string key)
    {
        return _states.TryGetValue(key, out var state) ? state : null;
    }

    /// <summary>
    /// Updates the state for a given endpoint key with new rate limit information.
    /// </summary>
    /// <param name="key">The endpoint key.</param>
    /// <param name="info">The new rate limit information.</param>
    public void UpdateState(string key, RateLimitInfo info)
    {
        var now = DateTimeOffset.UtcNow;
        var newState = new RateLimitState(info, now);

        _states.AddOrUpdate(key, newState, (_, _) => newState);

        // Periodically clean up stale entries (only if no cleanup is already in progress)
        if (Interlocked.Increment(ref _updateCounter) % CleanupFrequency == 0)
        {
            // Use CompareExchange to ensure only one cleanup runs at a time.
            // This prevents accumulation of cleanup tasks under high load.
            if (Interlocked.CompareExchange(ref _cleanupInProgress, 1, 0) == 0)
            {
                // Run cleanup on a background thread to avoid blocking the caller
                // Exceptions during cleanup are non-critical and should not crash the application
                _ = Task.Run(() =>
                {
                    try
                    {
                        RemoveStaleEntries(StaleEntryMaxAge);
                    }
                    catch
                    {
                        // Cleanup failures are non-critical - stale entries will be cleaned up on next attempt
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _cleanupInProgress, 0);
                    }
                });
            }
        }
    }

    /// <summary>
    /// Gets the most recent rate limit info for a given endpoint key.
    /// </summary>
    /// <param name="key">The endpoint key.</param>
    /// <returns>The most recent rate limit info, or default if not tracked.</returns>
    public RateLimitInfo GetRateLimitInfo(string key)
    {
        return _states.TryGetValue(key, out var state) ? state.Info : default;
    }

    /// <inheritdoc />
    public IEnumerable<string> TrackedEndpoints => _states.Keys;

    /// <inheritdoc />
    public IEnumerable<RateLimitInfo> GetAllStates() => _states.Values.Select(s => s.Info);

    /// <summary>
    /// Clears all tracked state.
    /// </summary>
    public void Clear()
    {
        _states.Clear();
    }

    /// <summary>
    /// Removes state for a specific endpoint.
    /// </summary>
    /// <param name="key">The endpoint key to remove.</param>
    /// <returns>True if the state was removed; otherwise false.</returns>
    public bool Remove(string key)
    {
        return _states.TryRemove(key, out _);
    }

    /// <summary>
    /// Removes stale entries older than the specified duration.
    /// </summary>
    /// <param name="maxAge">The maximum age of entries to keep.</param>
    /// <returns>The number of entries removed.</returns>
    public int RemoveStaleEntries(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var keysToRemove = _states
            .Where(kvp => kvp.Value.LastUpdated < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        int removed = 0;
        foreach (var key in keysToRemove)
        {
            if (_states.TryRemove(key, out _))
            {
                removed++;
            }
        }

        return removed;
    }
}

/// <summary>
/// Represents the rate limit state for a single endpoint.
/// </summary>
internal readonly record struct RateLimitState(RateLimitInfo Info, DateTimeOffset LastUpdated)
{
    /// <summary>
    /// Gets the time elapsed since the window reset time was recorded.
    /// </summary>
    public TimeSpan ElapsedSinceUpdate => DateTimeOffset.UtcNow - LastUpdated;

    /// <summary>
    /// Gets the estimated remaining reset time, accounting for elapsed time since the state was recorded.
    /// </summary>
    public TimeSpan EstimatedRemainingReset
    {
        get
        {
            var elapsed = ElapsedSinceUpdate;
            var resetTime = TimeSpan.FromSeconds(Info.ResetSeconds);
            var remaining = resetTime - elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Gets whether the rate limit window has likely reset since this state was recorded.
    /// </summary>
    public bool HasLikelyReset => Info.ResetSeconds > 0 && ElapsedSinceUpdate.TotalSeconds >= Info.ResetSeconds;
}
