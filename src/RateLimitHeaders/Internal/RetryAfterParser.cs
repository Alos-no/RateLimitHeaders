using System.Globalization;
using System.Net;

namespace RateLimitHeaders.Internal;

/// <summary>
/// Utility for parsing Retry-After headers from HTTP responses.
/// </summary>
/// <remarks>
/// <para>
/// Retry-After carries either delta-seconds or an HTTP-date (RFC 9110 section 10.2.3).
/// Dates are parsed format-exact against the three shapes RFC 9110 section 5.6.7 obliges
/// a recipient to accept (IMF-fixdate, the obsolete RFC 850 shape, and the obsolete
/// asctime shape); anything else, ISO 8601 included, is rejected. The RFC 850 shape's
/// two-digit year is interpreted with the sliding window the same section mandates: a
/// year that would land more than 50 years in the future reads as the most recent past
/// year with the same last two digits.
/// </para>
/// <para>
/// A past or present date parses successfully with a zero delay, so the precedence
/// override over RateLimit headers still applies observably. Any parsed delay is
/// clamped at <see cref="MaxRetryAfterDelay"/> so a hostile or mistaken header cannot
/// produce a wait that overflows <see cref="Task.Delay(TimeSpan)"/>.
/// </para>
/// </remarks>
internal static class RetryAfterParser
{
    /// <summary>
    /// The cap applied to every parsed Retry-After delay: 30 days. No client should
    /// honor a longer stop, and values near <see cref="int.MaxValue"/> seconds would
    /// overflow <see cref="Task.Delay(TimeSpan)"/> downstream.
    /// </summary>
    public static readonly TimeSpan MaxRetryAfterDelay = TimeSpan.FromDays(30);

    // IMF-fixdate: "Fri, 14 Aug 2026 12:00:00 GMT"
    private const string ImfFixdateFormat = "ddd, dd MMM yyyy HH:mm:ss 'GMT'";

    // Obsolete RFC 850 shape after the two-digit year is mapped to four digits:
    // "Friday, 14-Aug-2026 11:55:00 GMT"
    private const string Rfc850FourDigitYearFormat = "dddd, dd-MMM-yyyy HH:mm:ss 'GMT'";

    // Obsolete asctime shape: "Fri Aug 14 11:55:00 2026". A single-digit day is
    // space-padded to width two ("Fri Aug  4 11:55:00 2026"), hence the second format
    // with its literal double space; no other whitespace variation is accepted.
    private static readonly string[] AsctimeFormats =
    [
        "ddd MMM dd HH:mm:ss yyyy",
        "ddd MMM  d HH:mm:ss yyyy",
    ];

    private const DateTimeStyles UtcStyles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    /// <summary>
    /// Determines whether a Retry-After header on a response with the given status code
    /// should be treated as rate limit state, per the default status set
    /// (<see cref="RateLimitDefaults.RetryAfterStatusCodes"/>).
    /// </summary>
    public static bool ShouldHonor(HttpStatusCode statusCode) =>
        ShouldHonor(statusCode, RateLimitDefaults.RetryAfterStatusCodes);

    /// <summary>
    /// Determines whether a Retry-After header on a response with the given status code
    /// should be treated as rate limit state, per a caller-configured status set.
    /// </summary>
    public static bool ShouldHonor(HttpStatusCode statusCode, IEnumerable<int> honoredStatusCodes)
    {
        ArgumentNullException.ThrowIfNull(honoredStatusCodes);
        return honoredStatusCodes.Contains((int)statusCode);
    }

    /// <summary>
    /// Attempts to extract the Retry-After delay from a response, honoring only the
    /// configured status codes.
    /// </summary>
    /// <param name="response">The HTTP response to examine.</param>
    /// <param name="now">The current instant, used to convert an HTTP-date into a delay.</param>
    /// <param name="honoredStatusCodes">The status codes on which Retry-After is honored.</param>
    /// <param name="delay">When successful, the time to wait (zero for past dates), clamped at <see cref="MaxRetryAfterDelay"/>.</param>
    /// <returns>True if a valid Retry-After value was found on an honored status; otherwise false.</returns>
    public static bool TryGetRetryAfterDelay(
        HttpResponseMessage response,
        DateTimeOffset now,
        IEnumerable<int> honoredStatusCodes,
        out TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(response);
        delay = TimeSpan.Zero;

        if (!ShouldHonor(response.StatusCode, honoredStatusCodes))
        {
            return false;
        }

        if (!response.Headers.TryGetValues("Retry-After", out var values))
        {
            return false;
        }

        // Retry-After is a singleton field (RFC 9110 section 10.2.3): more than one value
        // is malformed and is rejected whole, never salvaged by picking one
        string? single = null;
        foreach (var value in values)
        {
            if (single is not null)
            {
                return false;
            }

            single = value;
        }

        return TryParseRetryAfterValue(single, now, out delay);
    }

    /// <summary>
    /// Attempts to parse a single Retry-After header value into a delay.
    /// </summary>
    /// <param name="value">The raw header value (delta-seconds or an HTTP-date).</param>
    /// <param name="now">The current instant, used to convert an HTTP-date into a delay.</param>
    /// <param name="delay">When successful, the time to wait (zero for past or present dates), clamped at <see cref="MaxRetryAfterDelay"/>.</param>
    /// <returns>True if the value is valid delta-seconds or one of the three RFC 9110 date shapes; otherwise false.</returns>
    public static bool TryParseRetryAfterValue(string? value, DateTimeOffset now, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        // Delta-seconds: a non-empty run of ASCII digits (no sign, no decimal point).
        // A value too large for long still means "a very long time" and clamps.
        if (IsAllAsciiDigits(trimmed))
        {
            delay = long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? ClampSeconds(seconds)
                : MaxRetryAfterDelay;
            return true;
        }

        if (TryParseHttpDate(trimmed, now, out var instant))
        {
            var remaining = instant - now;
            delay = remaining <= TimeSpan.Zero
                ? TimeSpan.Zero
                : (remaining > MaxRetryAfterDelay ? MaxRetryAfterDelay : remaining);
            return true;
        }

        return false;
    }

    private static bool IsAllAsciiDigits(string value)
    {
        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private static TimeSpan ClampSeconds(long seconds) =>
        seconds >= (long)MaxRetryAfterDelay.TotalSeconds
            ? MaxRetryAfterDelay
            : TimeSpan.FromSeconds(seconds);

    private static bool TryParseHttpDate(string value, DateTimeOffset now, out DateTimeOffset instant)
    {
        // HTTP-dates are always in UTC; the trailing "GMT" is a literal, so AssumeUniversal
        // supplies the offset.
        return DateTimeOffset.TryParseExact(value, ImfFixdateFormat, CultureInfo.InvariantCulture, UtcStyles, out instant)
            || TryParseRfc850Date(value, now, out instant)
            || DateTimeOffset.TryParseExact(value, AsctimeFormats, CultureInfo.InvariantCulture, UtcStyles, out instant);
    }

    /// <summary>
    /// Parses the obsolete RFC 850 shape ("Friday, 14-Aug-26 11:55:00 GMT"). The two-digit
    /// year is mapped to the four-digit year with the same last two digits that is not more
    /// than 50 years in the future of <paramref name="now"/> (RFC 9110 section 5.6.7;
    /// .NET's own two-digit-year pivot is fixed at 2049 and would misread or reject dates
    /// past it), then the rewritten value is parsed format-exact so the weekday is still
    /// validated against the mapped date.
    /// </summary>
    private static bool TryParseRfc850Date(string value, DateTimeOffset now, out DateTimeOffset instant)
    {
        instant = default;

        var parts = value.Split(' ');
        if (parts.Length != 4)
        {
            return false;
        }

        var dateParts = parts[1].Split('-');
        if (dateParts.Length != 3
            || dateParts[2].Length != 2
            || !int.TryParse(dateParts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var twoDigitYear))
        {
            return false;
        }

        var year = (now.Year / 100) * 100 + twoDigitYear;
        if (year > now.Year + 50)
        {
            year -= 100;
        }

        var rewritten = $"{parts[0]} {dateParts[0]}-{dateParts[1]}-{year.ToString("D4", CultureInfo.InvariantCulture)} {parts[2]} {parts[3]}";
        return DateTimeOffset.TryParseExact(rewritten, Rfc850FourDigitYearFormat, CultureInfo.InvariantCulture, UtcStyles, out instant);
    }
}
