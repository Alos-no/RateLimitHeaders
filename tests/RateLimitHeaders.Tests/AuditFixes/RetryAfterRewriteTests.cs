using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RateLimitHeaders.Http;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Tests.Fixtures;

namespace RateLimitHeaders.Tests.AuditFixes;

/// <summary>
/// Scenarios PARSE33-PARSE41 and PARSE46-PARSE47 from PLAN-audit-fixes.md (tasks T6 and T7):
/// <c>RetryAfterParser</c> rewritten to a <see cref="TimeSpan"/> result with past dates as
/// zero wait (so the precedence override still applies), a 30-day clamp
/// (<c>RetryAfterParser.MaxRetryAfterDelay</c>), format-exact parsing of the three RFC 9110
/// date shapes only, and a configurable honored-status set defaulting to
/// {403, 408, 429, 503} (findings AUD-13, AUD-14, AUD-15 in TRACKER-adversarial-audit.md).
/// </summary>
public class RetryAfterRewriteTests
{
    // 2026-08-14 is a Friday; every date row below uses this fixed instant as "now".
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    // PARSE33: a past HTTP-date means wait zero, not "no header".
    // Red baseline today: the old parser returned false for any past date.
    [Fact]
    public void PastHttpDate_MeansZeroWait()
    {
        var result = RetryAfterParser.TryParseRetryAfterValue("Fri, 14 Aug 2026 11:55:00 GMT", Now, out var delay);

        result.Should().BeTrue();
        delay.Should().Be(TimeSpan.Zero);
    }

    // PARSE34: a date equal to now yields zero wait (the old parser required strictly greater).
    [Fact]
    public void DateEqualToNow_YieldsZeroWait()
    {
        var result = RetryAfterParser.TryParseRetryAfterValue("Fri, 14 Aug 2026 12:00:00 GMT", Now, out var delay);

        result.Should().BeTrue();
        delay.Should().Be(TimeSpan.Zero);
    }

    // PARSE35: a far-future date is clamped at 30 days, not saturated to int.MaxValue seconds.
    [Fact]
    public void FarFutureDate_IsClampedAtThirtyDays()
    {
        var result = RetryAfterParser.TryParseRetryAfterValue("Fri, 31 Dec 9999 23:59:59 GMT", Now, out var delay);

        result.Should().BeTrue();
        delay.Should().Be(RetryAfterParser.MaxRetryAfterDelay);
        RetryAfterParser.MaxRetryAfterDelay.Should().Be(TimeSpan.FromDays(30));
    }

    // PARSE36: delta-seconds above int.MaxValue are honored and clamped, not rejected.
    [Fact]
    public void DeltaSecondsAboveInt32_AreHonoredAndClamped()
    {
        var result = RetryAfterParser.TryParseRetryAfterValue("999999999999", Now, out var delay);

        result.Should().BeTrue();
        delay.Should().Be(TimeSpan.FromDays(30));
    }

    // PARSE37: non-HTTP date formats are rejected (DateTimeOffset.TryParse accepted ISO 8601).
    [Fact]
    public void Iso8601Date_IsRejected()
    {
        var result = RetryAfterParser.TryParseRetryAfterValue("2126-08-14T12:00:00.0000000+00:00", Now, out var delay);

        result.Should().BeFalse();
        delay.Should().Be(TimeSpan.Zero);
    }

    // PARSE46: the obsolete RFC 850 date shape is accepted, as RFC 9110 requires.
    [Fact]
    public void Rfc850Date_IsAccepted()
    {
        var result = RetryAfterParser.TryParseRetryAfterValue("Friday, 14-Aug-26 11:55:00 GMT", Now, out var delay);

        result.Should().BeTrue();
        delay.Should().Be(TimeSpan.Zero, "the instant is in the past");
    }

    // PARSE47: the obsolete asctime date shape is accepted, as RFC 9110 requires.
    [Fact]
    public void AsctimeDate_IsAccepted()
    {
        var result = RetryAfterParser.TryParseRetryAfterValue("Fri Aug 14 11:55:00 2026", Now, out var delay);

        result.Should().BeTrue();
        delay.Should().Be(TimeSpan.Zero, "the instant is in the past");
    }

    // PARSE40: the default status set is exactly {403, 408, 429, 503}.
    [Fact]
    public void DefaultStatusSet_IsExactlyTheFourCodes()
    {
        RateLimitDefaults.RetryAfterStatusCodes.Should().Equal(403, 408, 429, 503);
        RetryAfterParser.ShouldHonor(HttpStatusCode.InternalServerError).Should().BeFalse();
        RetryAfterParser.ShouldHonor(HttpStatusCode.Forbidden).Should().BeTrue();
        RetryAfterParser.ShouldHonor(HttpStatusCode.RequestTimeout).Should().BeTrue();
        RetryAfterParser.ShouldHonor(HttpStatusCode.TooManyRequests).Should().BeTrue();
        RetryAfterParser.ShouldHonor(HttpStatusCode.ServiceUnavailable).Should().BeTrue();
    }

    private static (FakeTimeProvider Clock, RateLimitStateTracker Tracker, MockHttpHandler Mock, HttpClient Client, RateLimitAwareOptions Options)
        CreateHandlerFixture()
    {
        var clock = new FakeTimeProvider(Now);
        var tracker = new RateLimitStateTracker(clock);
        var options = new RateLimitAwareOptions
        {
            TrackStatePerEndpoint = false,
            TimeProvider = clock
        };
        var mock = new MockHttpHandler();
        var handler = new RateLimitAwareHandler(options, tracker, NullLogger.Instance)
        {
            InnerHandler = mock
        };
        return (clock, tracker, mock, new HttpClient(handler), options);
    }

    // PARSE39: a 403 with Retry-After (the shape GitHub sends on secondary limits)
    // produces tracked state. Red baseline today: nothing is written on a 403.
    [Fact]
    public async Task Forbidden_WithRetryAfter_ProducesTrackedState()
    {
        var (_, tracker, mock, client, _) = CreateHandlerFixture();
        var response = MockHttpHandler.CreateNoRateLimitResponse(HttpStatusCode.Forbidden);
        response.Headers.Add("Retry-After", "60");
        mock.QueueResponse(response);

        await client.GetAsync("http://example.com/api/test");
        var state = tracker.GetRateLimitInfo("global");

        state.IsValid.Should().BeTrue();
        state.IsExhausted.Should().BeTrue();
        state.ResetSeconds.Should().Be(60);
        state.HasRetryAfter.Should().BeTrue();
    }

    // PARSE41: a caller can widen the honored-status set and the handler consults it.
    [Fact]
    public async Task WidenedStatusSet_IsConsultedByTheHandler()
    {
        var (_, tracker, mock, client, options) = CreateHandlerFixture();
        options.RetryAfterStatusCodes = [200];
        var response = MockHttpHandler.CreateNoRateLimitResponse(HttpStatusCode.OK);
        response.Headers.Add("Retry-After", "30");
        mock.QueueResponse(response);

        await client.GetAsync("http://example.com/api/test");
        var state = tracker.GetRateLimitInfo("global");

        state.IsValid.Should().BeTrue();
        state.ResetSeconds.Should().Be(30);
        state.HasRetryAfter.Should().BeTrue();
    }

    // PARSE38: a past-dated Retry-After on a 429 still applies the precedence override
    // observably (the callback sees Remaining 0 and ResetSeconds 0), and the tracker's
    // adjusted read is immediately invalid, so the next request is not delayed.
    // Red baseline today: the past date reads as "no header" and the callback sees Remaining 5.
    [Fact]
    public async Task PastDatedRetryAfter_StillAppliesThePrecedenceOverride()
    {
        var (_, tracker, mock, client, options) = CreateHandlerFixture();
        RateLimitInfo? captured = null;
        options.OnRateLimitInfo = args =>
        {
            captured = args.RateLimitInfo;
            return ValueTask.CompletedTask;
        };

        var response = MockHttpHandler.CreateNoRateLimitResponse(HttpStatusCode.TooManyRequests);
        response.Headers.Add("RateLimit", "\"default\";r=5;t=60");
        response.Headers.Add("Retry-After", "Fri, 14 Aug 2026 11:55:00 GMT");
        mock.QueueResponse(response);
        mock.QueueResponse(MockHttpHandler.CreateNoRateLimitResponse());

        await client.GetAsync("http://example.com/api/test");

        captured.Should().NotBeNull();
        var info = captured!.Value;
        info.Remaining.Should().Be(0);
        info.IsExhausted.Should().BeTrue();
        info.HasRetryAfter.Should().BeTrue();
        info.ResetSeconds.Should().Be(0);

        // A zero-second stop is already elapsed under the adjusted-read rule, so the
        // next request goes through without any wait (no clock advance needed).
        tracker.GetRateLimitInfo("global").IsValid.Should().BeFalse();
        await client.GetAsync("http://example.com/api/test");
        mock.RequestCount.Should().Be(2);
    }
}
