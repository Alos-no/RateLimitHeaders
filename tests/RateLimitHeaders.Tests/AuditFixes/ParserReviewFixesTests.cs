using System.Net;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Throttling;

namespace RateLimitHeaders.Tests.AuditFixes;

/// <summary>
/// Regression tests for the fixes adopted from the cross-model review of the strict parser
/// rewrite (the adjudication ledger lives in TRACKER-adversarial-audit.md, the audit
/// findings ledger): the byte-sequence path validates the base64 alphabet itself and
/// synthesizes missing padding (RFC 9651 section 4.2.7), the decoded-text guard also
/// rejects Unicode line separators and format characters, duplicate policy names resolve
/// last-wins in both header fields, quota-only entries order by quota and never read as
/// exhausted, the RFC 850 two-digit year uses RFC 9110's sliding 50-year window, and
/// repeated Retry-After values are rejected as one malformed singleton field.
/// </summary>
public class ParserReviewFixesTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    // Convert.TryFromBase64String silently skips whitespace; the parser must reject it
    // (RFC 9651 admits only the base64 alphabet between the colons), or the base64
    // fallback text could carry a tab or line break into logs.
    [Theory]
    [InlineData("\"a\";r=5;t=30;pk=:dGVu YW50:")]
    [InlineData("\"a\";r=5;t=30;pk=:dGVu\tYW50:")]
    public void Base64WithWhitespace_VoidsTheField(string rateLimitHeader)
    {
        RateLimitHeaderParser.Parse(rateLimitHeader, null).IsValid.Should().BeFalse();
    }

    // RFC 9651 section 4.2.7 synthesizes missing padding instead of failing, so a
    // conforming unpadded pk must not cost the client the whole field.
    [Fact]
    public void UnpaddedBase64_IsAcceptedAndDecoded()
    {
        var info = RateLimitHeaderParser.Parse("\"a\";r=5;t=30;pk=:dGVuYW50LTE:", null);

        info.IsValid.Should().BeTrue();
        info.PartitionKey.Should().Be("tenant-1");
    }

    // U+2028 LINE SEPARATOR (base64 4oCo) renders as a line break in common log viewers
    // and U+202E RIGHT-TO-LEFT OVERRIDE (base64 4oCu) reverses display order; neither is
    // a Cc control character, so the guard must reject them by Unicode category and fall
    // back to the base64 text.
    [Theory]
    [InlineData(":4oCo:", "4oCo")]
    [InlineData(":4oCu:", "4oCu")]
    public void LineSeparatorAndBidiOverrideBytes_StayInBase64Form(string pkValue, string expectedText)
    {
        var info = RateLimitHeaderParser.Parse($"\"a\";r=5;t=30;pk={pkValue}", null);

        info.IsValid.Should().BeTrue();
        info.PartitionKey.Should().Be(expectedText);
    }

    // Duplicate policy names resolve last-wins in both fields; a repeated name never
    // emits two results and never lets the superseded first value win the selection.
    [Fact]
    public void DuplicatePolicyNames_ResolveLastWins_InBothFields()
    {
        var policyOnly = RateLimitHeaderParser.ParseAll(null, "\"a\";q=1;w=60,\"a\";q=2;w=60");
        policyOnly.Should().ContainSingle();
        policyOnly[0].Quota.Should().Be(2);

        var rateLimitOnly = RateLimitHeaderParser.ParseAll("\"a\";r=5;t=30,\"a\";r=1;t=30", null);
        rateLimitOnly.Should().ContainSingle();
        rateLimitOnly[0].Remaining.Should().Be(1);
    }

    // Quota-only entries carry no remaining count, no fraction, and no reset, so without
    // a quota rung the comparator would order them alphabetically; the names here are
    // chosen so alphabetical order and quota order disagree.
    [Fact]
    public void QuotaOnlyEntries_OrderByQuota_NotByName()
    {
        var all = RateLimitHeaderParser.ParseAll(null, "\"zsmall\";q=100;w=60,\"abig\";q=1000;w=86400");

        all.Select(i => i.PolicyName).Should().Equal("zsmall", "abig");
        RateLimitHeaderParser.Parse(null, "\"zsmall\";q=100;w=60,\"abig\";q=1000;w=86400")
            .Quota.Should().Be(100);
    }

    // An empty byte sequence (pk=::) on the RateLimit entry is not a real override and
    // must not suppress the policy entry's partition key through the null-only merge check.
    [Fact]
    public void EmptyPartitionKeyOnRateLimitEntry_DoesNotSuppressThePolicyValue()
    {
        var info = RateLimitHeaderParser.Parse(
            "\"a\";r=5;t=30;pk=::",
            "\"a\";q=100;w=60;pk=:dGVuYW50:");

        info.PartitionKey.Should().Be("tenant");
    }

    // A quota-only entry advertises a limit without reporting consumption. Its Remaining
    // getter returns 0 for compatibility, but it must not read as exhausted, low, or
    // throttleable.
    [Fact]
    public void QuotaOnlyEntry_DoesNotReadAsExhaustedOrLowQuota()
    {
        var info = RateLimitHeaderParser.Parse(null, "\"api\";q=100;w=60");

        info.IsValid.Should().BeTrue();
        info.HasRemaining.Should().BeFalse();
        info.IsExhausted.Should().BeFalse();
        info.IsQuotaLow().Should().BeFalse();
        new PercentageThrottlingAlgorithm().Evaluate(info).ShouldThrottle.Should().BeFalse();
    }

    // RFC 9110 section 5.6.7: a two-digit year that would land more than 50 years in the
    // future reads as the most recent past year with the same digits. .NET's fixed pivot
    // of 2049 would read year 70 as 1970; from 2026, year 70 must mean 2070 (a Thursday,
    // so the 1970 weekday "Friday" is a malformed date, not a past instant).
    [Fact]
    public void Rfc850TwoDigitYear_UsesTheSlidingFiftyYearWindow()
    {
        RetryAfterParser.TryParseRetryAfterValue("Thursday, 14-Aug-70 12:00:00 GMT", Now, out var delay)
            .Should().BeTrue();
        delay.Should().Be(RetryAfterParser.MaxRetryAfterDelay, "2070 is in the future, clamped at 30 days");

        RetryAfterParser.TryParseRetryAfterValue("Friday, 14-Aug-70 12:00:00 GMT", Now, out _)
            .Should().BeFalse("Friday is 1970's weekday; the year maps to 2070, which is a Thursday");
    }

    // The asctime shape allows exactly one space between fields, with a single-digit day
    // space-padded to width two; surplus whitespace is not one of the three RFC 9110
    // shapes and must not become trusted state.
    [Fact]
    public void AsctimeWhitespace_IsFormatExact()
    {
        RetryAfterParser.TryParseRetryAfterValue("Tue Aug  4 12:00:00 2026", Now, out var delay)
            .Should().BeTrue("a space-padded single-digit day is the canonical asctime shape");
        delay.Should().Be(TimeSpan.Zero, "the instant is in the past");

        RetryAfterParser.TryParseRetryAfterValue("Fri   Aug   14   12:00:00   2026", Now, out _)
            .Should().BeFalse();
    }

    // Retry-After is a singleton field (RFC 9110); two values are one malformed field and
    // must not be salvaged by honoring the first.
    [Fact]
    public void MultipleRetryAfterValues_AreRejected()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "60");
        response.Headers.TryAddWithoutValidation("Retry-After", "120");

        RetryAfterParser.TryGetRetryAfterDelay(response, Now, RateLimitDefaults.RetryAfterStatusCodes, out var delay)
            .Should().BeFalse();
        delay.Should().Be(TimeSpan.Zero);
    }

    // The public Retry-After construction methods clamp negative inputs to zero, so no
    // caller-computed value can push a negative TimeSpan into Task.Delay.
    [Fact]
    public void NegativeRetryAfterConstruction_ClampsToZero()
    {
        RateLimitInfo.CreateFromRetryAfter(-5).ResetSeconds.Should().Be(0);
        RateLimitInfo.CreateFromRetryAfter(TimeSpan.FromSeconds(-5)).ResetAfter.Should().Be(TimeSpan.Zero);
        default(RateLimitInfo).WithRetryAfterOverride(TimeSpan.FromSeconds(-5)).ResetAfter.Should().Be(TimeSpan.Zero);
    }

    // A duplicate parameter key overwrites its earlier entry in place (RFC 9651 section
    // 4.2.3.2 step 7 keeps the original position) instead of moving to the end.
    [Fact]
    public void DuplicateParameter_KeepsItsOriginalPosition()
    {
        StructuredFieldParser.TryParseList("\"a\";r=5;t=30;r=1", out var items).Should().BeTrue();

        items[0].Parameters.Select(p => p.Key).Should().Equal("r", "t");
        items[0].TryGetParameter("r", out var r).Should().BeTrue();
        r.IntegerValue.Should().Be(1);
    }

    // RFC 9651 parses an empty (or all-space) field value as a valid, empty list; only a
    // null input (no field at all) reports failure.
    [Fact]
    public void EmptyFieldValue_IsAValidEmptyList()
    {
        StructuredFieldParser.TryParseList(string.Empty, out var empty).Should().BeTrue();
        empty.Should().BeEmpty();

        StructuredFieldParser.TryParseList("   ", out var spaces).Should().BeTrue();
        spaces.Should().BeEmpty();

        StructuredFieldParser.TryParseList(null, out _).Should().BeFalse();
    }

    // Repeated header lines combine verbatim with a comma (RFC 9110 field combination):
    // a valid line followed by an empty line is a field with a trailing comma, which
    // strict parsing rejects whole instead of salvaging the valid line.
    [Fact]
    public void EmptyRepeatedHeaderLine_VoidsTheCombinedField()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("RateLimit", "\"a\";r=5;t=30");
        response.Headers.TryAddWithoutValidation("RateLimit", "");

        RateLimitHeaderParser.Parse(response).IsValid.Should().BeFalse();
    }
}
