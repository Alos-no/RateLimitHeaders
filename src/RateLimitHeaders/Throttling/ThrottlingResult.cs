namespace RateLimitHeaders.Throttling;

/// <summary>
/// Represents the result of a throttling algorithm evaluation.
/// </summary>
/// <param name="ShouldThrottle">Whether the request should be throttled (delayed).</param>
/// <param name="Delay">The recommended delay before proceeding with the request.</param>
/// <param name="Reason">Optional human-readable reason for the throttling decision.</param>
public readonly record struct ThrottlingResult(bool ShouldThrottle, TimeSpan Delay, string? Reason = null)
{
    /// <summary>
    /// A result indicating no throttling is needed.
    /// </summary>
    public static readonly ThrottlingResult NoThrottle = new(false, TimeSpan.Zero);

    /// <summary>
    /// Creates a throttling result with the specified delay.
    /// </summary>
    /// <param name="delay">The delay to apply.</param>
    /// <param name="reason">Optional reason for throttling.</param>
    /// <returns>A <see cref="ThrottlingResult"/> indicating throttling should occur.</returns>
    public static ThrottlingResult Throttle(TimeSpan delay, string? reason = null) =>
        new(true, delay, reason);

    /// <summary>
    /// Creates a throttling result with the specified delay in milliseconds.
    /// </summary>
    /// <param name="delayMs">The delay in milliseconds.</param>
    /// <param name="reason">Optional reason for throttling.</param>
    /// <returns>A <see cref="ThrottlingResult"/> indicating throttling should occur.</returns>
    public static ThrottlingResult Throttle(int delayMs, string? reason = null) =>
        new(true, TimeSpan.FromMilliseconds(delayMs), reason);
}
