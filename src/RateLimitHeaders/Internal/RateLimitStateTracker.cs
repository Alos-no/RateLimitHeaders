using System.Collections.Concurrent;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Throttling;

namespace RateLimitHeaders.Internal;

/// <summary>
/// Thread-safe tracker for per-endpoint rate limit state.
/// </summary>
/// <remarks>
/// Writes store the raw parsed info together with the arrival instant. Reads return a
/// time-adjusted view: the elapsed time since the response arrived is subtracted from the
/// stored reset value, and a state whose window has already elapsed reads as invalid
/// (default). Raw snapshots stay reachable through <see cref="GetState"/>.
/// </remarks>
internal sealed class RateLimitStateTracker : IRateLimitStateProvider
{
    private readonly ConcurrentDictionary<string, RateLimitState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private int _updateCounter;
    private int _cleanupInProgress;

    /// <summary>
    /// Initializes a new instance using the system clock.
    /// </summary>
    public RateLimitStateTracker()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance using the given clock.
    /// </summary>
    /// <param name="timeProvider">The clock used for state timestamps and staleness checks.</param>
    public RateLimitStateTracker(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

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
    /// Gets the current raw state for a given endpoint key, with no time adjustment.
    /// </summary>
    /// <param name="key">The endpoint key.</param>
    /// <returns>The stored state, or null if no state exists.</returns>
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
        var now = _timeProvider.GetUtcNow();
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
    /// Gets the time-adjusted rate limit info for a given endpoint key.
    /// </summary>
    /// <param name="key">The endpoint key.</param>
    /// <returns>
    /// The stored info with the elapsed time subtracted from its reset value; a default
    /// (invalid) instance when the key is not tracked or the stored window has already elapsed.
    /// </returns>
    public RateLimitInfo GetRateLimitInfo(string key)
    {
        return _states.TryGetValue(key, out var state)
            ? AdjustForElapsedTime(state, _timeProvider.GetUtcNow())
            : default;
    }

    /// <summary>
    /// Gets the full throttling context for a given endpoint key: the time-adjusted info plus
    /// the observation instant and the exact (sub-second) time until the window resets.
    /// </summary>
    /// <param name="key">The endpoint key.</param>
    /// <param name="context">The throttling context when the state is still live.</param>
    /// <returns>
    /// False when the key is not tracked, the stored info is invalid, or the stored window
    /// has already elapsed (the state must no longer drive decisions); true otherwise.
    /// </returns>
    public bool TryGetThrottlingContext(string key, out ThrottlingContext context)
    {
        context = default;
        if (!_states.TryGetValue(key, out var state) || !state.Info.IsValid)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        var elapsed = now - state.LastUpdated;
        var info = state.Info;

        if (!info.HasResetSeconds)
        {
            // No reset value: the state never expires by window; report zero time-until-reset.
            context = new ThrottlingContext(info, state.LastUpdated, elapsed, TimeSpan.Zero, this);
            return true;
        }

        var window = TimeSpan.FromSeconds(Math.Min(info.ResetSeconds, RateLimitInfo.MaxResetSeconds));
        if (elapsed >= window)
        {
            // The window closed at its own reset instant; the state is spent.
            return false;
        }

        var timeUntilReset = window - elapsed;
        var adjusted = info with
        {
            ResetSeconds = Math.Max(1, (long)Math.Ceiling(timeUntilReset.TotalSeconds))
        };
        context = new ThrottlingContext(adjusted, state.LastUpdated, elapsed, timeUntilReset, this);
        return true;
    }

    /// <inheritdoc />
    public IEnumerable<string> TrackedEndpoints => _states.Keys;

    /// <inheritdoc />
    /// <remarks>Returns time-adjusted views and omits states whose window has already elapsed.</remarks>
    public IEnumerable<RateLimitInfo> GetAllStates()
    {
        var now = _timeProvider.GetUtcNow();
        return _states.Values
            .Select(s => AdjustForElapsedTime(s, now))
            .Where(info => info.IsValid);
    }

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
        var cutoff = _timeProvider.GetUtcNow() - maxAge;
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

    /// <summary>
    /// Produces the time-adjusted view of a stored state: subtracts the elapsed time from the
    /// stored reset value (never below 1 second while the window is live) and reports a state
    /// whose window has already elapsed as a default (invalid) instance. States without a
    /// reset value are returned as stored.
    /// </summary>
    private static RateLimitInfo AdjustForElapsedTime(RateLimitState state, DateTimeOffset now)
    {
        var info = state.Info;
        if (!info.IsValid || !info.HasResetSeconds)
        {
            return info;
        }

        var elapsed = now - state.LastUpdated;
        var window = TimeSpan.FromSeconds(Math.Min(info.ResetSeconds, RateLimitInfo.MaxResetSeconds));
        if (elapsed >= window)
        {
            return default;
        }

        return info with
        {
            ResetSeconds = Math.Max(1, (long)Math.Ceiling((window - elapsed).TotalSeconds))
        };
    }
}

/// <summary>
/// Represents the rate limit state for a single endpoint: the raw parsed info and the
/// instant the response carrying it arrived.
/// </summary>
internal readonly record struct RateLimitState(RateLimitInfo Info, DateTimeOffset LastUpdated);
