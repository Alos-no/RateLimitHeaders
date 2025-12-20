using System.Diagnostics.CodeAnalysis;
using RateLimitHeaders.Parsing;

namespace RateLimitHeaders.Events;

/// <summary>
/// Provides data for rate limit info events from the HTTP handler.
/// </summary>
/// <param name="RateLimitInfo">The parsed rate limit information.</param>
/// <param name="RequestUri">The URI of the request that triggered this event.</param>
/// <param name="Response">The HTTP response containing the rate limit headers.</param>
/// <remarks>
/// <para>
/// <strong>Important:</strong> Do not store the <see cref="Response"/> reference beyond the callback scope.
/// The response may be disposed after the callback completes. If you need response data for later use,
/// extract the required information during the callback and store only that data.
/// </para>
/// </remarks>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "EventArgs suffix is intentional for event data records")]
public readonly record struct RateLimitEventArgs(
    RateLimitInfo RateLimitInfo,
    Uri? RequestUri,
    HttpResponseMessage Response);

/// <summary>
/// Provides data for quota low warning events from the HTTP handler.
/// </summary>
/// <param name="RateLimitInfo">The parsed rate limit information.</param>
/// <param name="RequestUri">The URI of the request that triggered this event.</param>
/// <param name="RemainingPercentage">The remaining quota as a percentage (0.0 to 1.0).</param>
/// <param name="Threshold">The threshold that was exceeded.</param>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "EventArgs suffix is intentional for event data records")]
public readonly record struct QuotaLowEventArgs(
    RateLimitInfo RateLimitInfo,
    Uri? RequestUri,
    double RemainingPercentage,
    double Threshold);

/// <summary>
/// Provides data for throttling events from the HTTP handler.
/// </summary>
/// <param name="RateLimitInfo">The parsed rate limit information.</param>
/// <param name="RequestUri">The URI of the request being throttled.</param>
/// <param name="Delay">The delay being applied.</param>
/// <param name="Reason">The reason for throttling.</param>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "EventArgs suffix is intentional for event data records")]
public readonly record struct ThrottlingEventArgs(
    RateLimitInfo RateLimitInfo,
    Uri? RequestUri,
    TimeSpan Delay,
    string? Reason);
