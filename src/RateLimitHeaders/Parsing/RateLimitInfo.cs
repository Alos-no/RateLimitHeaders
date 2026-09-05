namespace RateLimitHeaders.Parsing;

/// <summary>
/// Represents parsed IETF RateLimit header information.
/// </summary>
/// <remarks>
/// This is a readonly record struct that is safe to use with default initialization.
/// When default-initialized, <see cref="IsValid"/> will be <c>false</c> and
/// <see cref="PolicyName"/> will be an empty string.
/// </remarks>
/// <example>
/// <para>Accessing rate limit info from a response context:</para>
/// <code><![CDATA[
/// var context = ResilienceContextPool.Shared.Get();
/// try
/// {
///     var response = await pipeline.ExecuteAsync(
///         async (ctx) => await httpClient.GetAsync("https://api.example.com/v1/data"),
///         context);
///
///     if (context.Properties.TryGetValue(RateLimitContextProperties.RateLimitInfoKey, out RateLimitInfo info))
///     {
///         Console.WriteLine($"Rate limit: {info.Remaining}/{info.Quota}");
///         Console.WriteLine($"Resets in: {info.ResetSeconds} seconds");
///
///         if (info.IsQuotaLow(0.2))
///         {
///             Console.WriteLine("Warning: Quota is below 20%!");
///         }
///     }
/// }
/// finally
/// {
///     ResilienceContextPool.Shared.Return(context);
/// }
/// ]]></code>
/// </example>
public readonly record struct RateLimitInfo
{
    /// <summary>
    /// The largest reset value (in seconds) that <see cref="ResetAfter"/> will convert:
    /// 365 days. Larger parsed values are legal 15-digit structured-field integers and are
    /// clamped here so the <see cref="TimeSpan"/> conversion can never overflow.
    /// </summary>
    public const long MaxResetSeconds = 31_536_000;

    private readonly string? _policyName;
    private readonly long? _remaining;
    private readonly long? _resetSeconds;
    private readonly long? _quota;
    private readonly long? _windowSeconds;

    /// <summary>The rate limit policy name (e.g., "default", "api-v2").</summary>
    /// <remarks>Returns an empty string if no policy name was parsed.</remarks>
    public string PolicyName
    {
        get => _policyName ?? string.Empty;
        init => _policyName = value;
    }

    /// <summary>Remaining requests allowed in the current window. Returns 0 when the server never sent the value; check <see cref="HasRemaining"/> to distinguish a sent zero from an absent value.</summary>
    public long Remaining
    {
        get => _remaining ?? 0;
        init => _remaining = value;
    }

    /// <summary>Seconds until the current window resets. Returns 0 when the server never sent the value; check <see cref="HasResetSeconds"/>.</summary>
    public long ResetSeconds
    {
        get => _resetSeconds ?? 0;
        init => _resetSeconds = value;
    }

    /// <summary>Maximum requests allowed per window (quota). Returns 0 when the server never sent the value; check <see cref="HasQuota"/>.</summary>
    public long Quota
    {
        get => _quota ?? 0;
        init => _quota = value;
    }

    /// <summary>Duration of the rate limit window in seconds. Returns 0 when the server never sent the value; check <see cref="HasWindowSeconds"/>.</summary>
    public long WindowSeconds
    {
        get => _windowSeconds ?? 0;
        init => _windowSeconds = value;
    }

    /// <summary>Whether the server sent a remaining-requests value (the <c>r</c> parameter).</summary>
    public bool HasRemaining => _remaining.HasValue;

    /// <summary>Whether the server sent a reset value (the <c>t</c> parameter or a Retry-After).</summary>
    public bool HasResetSeconds => _resetSeconds.HasValue;

    /// <summary>Whether the server sent a quota value (the <c>q</c> parameter).</summary>
    public bool HasQuota => _quota.HasValue;

    /// <summary>Whether the server sent a window length (the <c>w</c> parameter).</summary>
    public bool HasWindowSeconds => _windowSeconds.HasValue;

    /// <summary>
    /// Whether this state came from (or was overridden by) a Retry-After header,
    /// i.e. the server explicitly ordered the client to stop sending until the reset moment.
    /// </summary>
    public bool HasRetryAfter { get; init; }

    /// <summary>
    /// Whether the server reported zero remaining requests. A sent zero only:
    /// an absent remaining value reads as not exhausted even though <see cref="Remaining"/> returns 0.
    /// </summary>
    public bool IsExhausted => _remaining is 0;

    /// <summary>
    /// The remaining share of the quota (0.0 to 1.0), or <c>null</c> when the server sent
    /// no remaining count or no positive quota, so the share is unknown rather than healthy.
    /// </summary>
    public double? RemainingFraction =>
        HasRemaining && _quota is > 0 ? (double)Remaining / Quota : null;

    /// <summary>
    /// The time until the window resets, as a <see cref="TimeSpan"/> clamped at
    /// <see cref="MaxResetSeconds"/> (365 days). <see cref="TimeSpan.Zero"/> when no reset value was sent.
    /// </summary>
    public TimeSpan ResetAfter =>
        HasResetSeconds ? TimeSpan.FromSeconds(Math.Min(ResetSeconds, MaxResetSeconds)) : TimeSpan.Zero;

    /// <summary>
    /// The partition key identifying which partition this rate limit applies to.
    /// </summary>
    /// <remarks>
    /// Optional IETF parameter (pk). Used when rate limits are partitioned by different keys
    /// such as tenant ID, API key, or user ID. May be null if not provided by the server.
    /// </remarks>
    public string? PartitionKey { get; init; }

    /// <summary>
    /// The unit of measurement for the quota (e.g., "requests", "content-bytes").
    /// </summary>
    /// <remarks>
    /// Optional IETF parameter (qu). Default is "requests" when not specified.
    /// Common values include:
    /// <list type="bullet">
    /// <item><term>requests</term><description>Number of API requests</description></item>
    /// <item><term>content-bytes</term><description>Total bytes in request/response body</description></item>
    /// <item><term>tokens</term><description>Token count (for AI/ML APIs)</description></item>
    /// </list>
    /// </remarks>
    public string? QuotaUnit { get; init; }

    /// <summary>Whether at least one header was successfully parsed.</summary>
    public bool IsValid { get; init; }

    /// <summary>Gets the remaining quota as a percentage (0.0 to 1.0).</summary>
    public double GetRemainingPercentage() => Quota > 0 ? (double)Remaining / Quota : 1.0;

    /// <summary>
    /// Creates a minimal <see cref="RateLimitInfo"/> from a Retry-After header value.
    /// </summary>
    /// <param name="retryAfterSeconds">The number of seconds from the Retry-After header.</param>
    /// <returns>A valid <see cref="RateLimitInfo"/> with zero remaining quota.</returns>
    /// <remarks>
    /// This is used when a 429/503 response includes a Retry-After header but no RateLimit headers.
    /// The resulting info indicates the client should wait before retrying.
    /// </remarks>
    public static RateLimitInfo CreateFromRetryAfter(long retryAfterSeconds) => new()
    {
        PolicyName = "retry-after",
        Remaining = 0,
        ResetSeconds = retryAfterSeconds,
        HasRetryAfter = true,
        IsValid = true
    };

    /// <summary>
    /// Creates a minimal <see cref="RateLimitInfo"/> from a Retry-After delay.
    /// </summary>
    /// <param name="retryAfter">The delay from the Retry-After header.</param>
    /// <returns>A valid <see cref="RateLimitInfo"/> with zero remaining quota and <see cref="HasRetryAfter"/> set.</returns>
    public static RateLimitInfo CreateFromRetryAfter(TimeSpan retryAfter) =>
        CreateFromRetryAfter((long)Math.Ceiling(retryAfter.TotalSeconds));

    /// <summary>
    /// Creates a new <see cref="RateLimitInfo"/> with the Retry-After override applied.
    /// </summary>
    /// <param name="retryAfterSeconds">The Retry-After seconds that override the reset time.</param>
    /// <returns>A new instance with Remaining set to 0, ResetSeconds updated, and <see cref="HasRetryAfter"/> set.</returns>
    /// <remarks>
    /// Per IETF spec, Retry-After takes precedence over RateLimit headers when present.
    /// This typically occurs on 429/503 responses where the server explicitly tells
    /// the client how long to wait.
    /// </remarks>
    public RateLimitInfo WithRetryAfterOverride(long retryAfterSeconds) => this with
    {
        Remaining = 0,
        ResetSeconds = retryAfterSeconds,
        HasRetryAfter = true
    };

    /// <summary>Checks if remaining quota is at or below the specified threshold.</summary>
    /// <param name="threshold">The threshold percentage (0.0 to 1.0). Default is 0.1 (10%).</param>
    /// <returns>True if quota is low; false otherwise.</returns>
    public bool IsQuotaLow(double threshold = 0.1) => IsValid && Quota > 0 && GetRemainingPercentage() <= threshold;

    /// <inheritdoc />
    public override string ToString()
    {
        if (!IsValid)
        {
            return "RateLimit[Invalid]";
        }

        var result = $"RateLimit[{PolicyName}]: {Remaining}/{Quota} (Reset={ResetSeconds}s, Window={WindowSeconds}s)";

        if (!string.IsNullOrEmpty(PartitionKey))
        {
            result += $" pk={PartitionKey}";
        }

        if (!string.IsNullOrEmpty(QuotaUnit))
        {
            result += $" qu={QuotaUnit}";
        }

        return result;
    }
}
