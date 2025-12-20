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
    private readonly string? _policyName;

    /// <summary>The rate limit policy name (e.g., "default", "api-v2").</summary>
    /// <remarks>Returns an empty string if no policy name was parsed.</remarks>
    public string PolicyName
    {
        get => _policyName ?? string.Empty;
        init => _policyName = value;
    }

    /// <summary>Remaining requests allowed in the current window.</summary>
    public int Remaining { get; init; }

    /// <summary>Seconds until the current window resets.</summary>
    public int ResetSeconds { get; init; }

    /// <summary>Maximum requests allowed per window (quota).</summary>
    public int Quota { get; init; }

    /// <summary>Duration of the rate limit window in seconds.</summary>
    public int WindowSeconds { get; init; }

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
    public static RateLimitInfo CreateFromRetryAfter(int retryAfterSeconds) => new()
    {
        PolicyName = "retry-after",
        Remaining = 0,
        ResetSeconds = retryAfterSeconds,
        IsValid = true
    };

    /// <summary>
    /// Creates a new <see cref="RateLimitInfo"/> with the Retry-After override applied.
    /// </summary>
    /// <param name="retryAfterSeconds">The Retry-After seconds that override the reset time.</param>
    /// <returns>A new instance with Remaining set to 0 and ResetSeconds updated.</returns>
    /// <remarks>
    /// Per IETF spec, Retry-After takes precedence over RateLimit headers when present.
    /// This typically occurs on 429/503 responses where the server explicitly tells
    /// the client how long to wait.
    /// </remarks>
    public RateLimitInfo WithRetryAfterOverride(int retryAfterSeconds) => this with
    {
        Remaining = 0,
        ResetSeconds = retryAfterSeconds
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
