using RateLimitHeaders.Internal;

namespace RateLimitHeaders.Tests.AuditFixes;

/// <summary>
/// Scenarios PARSE01-PARSE09 from PLAN-audit-fixes.md (task T1): the new strict
/// structured-field list parser (RFC 9651) honors string escaping, resolves duplicate
/// parameter keys last-wins, carries 15-digit integers into <c>long</c>, decodes byte
/// sequences at parse time, and discards the entire field value on any malformed member,
/// with no partial result. This is the foundation for closing the header injection finding
/// (AUD-09 in TRACKER-adversarial-audit.md, the adversarial-audit findings ledger).
/// </summary>
public class StructuredFieldParserTests
{
    // PARSE01: an escaped quote inside a policy name cannot smuggle parameters.
    // Red baseline today: the regex splitter reads r=99999 out of the quoted text.
    [Fact]
    public void EscapedQuote_CannotSmuggleParameters()
    {
        var input = @"""pol\"";r=99999;t=0"";r=1;t=3600";

        StructuredFieldParser.TryParseList(input, out var items).Should().BeTrue();

        items.Should().ContainSingle();
        items[0].Value.Text.Should().Be(@"pol"";r=99999;t=0");

        items[0].Parameters.Select(p => p.Key).Should().Equal("r", "t");
        items[0].TryGetParameter("r", out var r).Should().BeTrue();
        r.IntegerValue.Should().Be(1);
        items[0].TryGetParameter("t", out var t).Should().BeTrue();
        t.IntegerValue.Should().Be(3600);
    }

    // PARSE02: an unterminated string voids the whole field.
    [Fact]
    public void UnterminatedString_VoidsTheField()
    {
        StructuredFieldParser.TryParseList(@"""unterminated;r=1", out var items).Should().BeFalse();
        items.Should().BeEmpty();
    }

    // PARSE03: duplicate parameter keys resolve last-wins per RFC 9651.
    [Fact]
    public void DuplicateParameterKeys_ResolveLastWins()
    {
        StructuredFieldParser.TryParseList(@"""a"";r=1;r=2", out var items).Should().BeTrue();

        items[0].Parameters.Should().ContainSingle(p => p.Key == "r");
        items[0].TryGetParameter("r", out var r).Should().BeTrue();
        r.IntegerValue.Should().Be(2);
    }

    // PARSE04: 15-digit integers pass; 16 digits void the field.
    [Fact]
    public void IntegerDigitLimit_IsFifteen()
    {
        StructuredFieldParser.TryParseList(@"""a"";q=999999999999999", out var accepted).Should().BeTrue();
        accepted[0].TryGetParameter("q", out var q).Should().BeTrue();
        q.IntegerValue.Should().Be(999_999_999_999_999);

        StructuredFieldParser.TryParseList(@"""a"";q=1000000000000000", out var rejected).Should().BeFalse();
        rejected.Should().BeEmpty();
    }

    // PARSE05: parameter names match as whole keys, so an unrelated key ending in "pk"
    // is never mistaken for the partition key.
    [Fact]
    public void ParameterNames_MatchAsWholeKeys()
    {
        StructuredFieldParser.TryParseList(@"""a"";spk=3;r=1", out var items).Should().BeTrue();

        items[0].Parameters.Select(p => p.Key).Should().Equal("spk", "r");
        items[0].TryGetParameter("pk", out _).Should().BeFalse();
    }

    // PARSE06: byte sequences decode at parse time; undecodable base64 voids the field.
    [Fact]
    public void ByteSequences_DecodeAtParseTime()
    {
        StructuredFieldParser.TryParseList(@"""a"";pk=:dGVuYW50LTE=:", out var decoded).Should().BeTrue();
        decoded[0].TryGetParameter("pk", out var pk).Should().BeTrue();
        pk.Kind.Should().Be(StructuredFieldValueKind.ByteSequence);
        pk.Text.Should().Be("tenant-1");

        StructuredFieldParser.TryParseList(@"""a"";pk=:!!!:", out var rejected).Should().BeFalse();
        rejected.Should().BeEmpty();
    }

    // PARSE07: a trailing comma or empty member voids the field.
    [Theory]
    [InlineData(@"""a"";r=1,")]
    [InlineData(@"""a"";r=1,,""b"";r=2")]
    public void TrailingCommaOrEmptyMember_VoidsTheField(string input)
    {
        StructuredFieldParser.TryParseList(input, out var items).Should().BeFalse();
        items.Should().BeEmpty();
    }

    // PARSE08: a backslash before anything but a quote or backslash voids the field.
    [Fact]
    public void IllegalEscape_VoidsTheField()
    {
        StructuredFieldParser.TryParseList(@"""a\n"";r=1", out var items).Should().BeFalse();
        items.Should().BeEmpty();
    }

    // PARSE09: the two legal escapes decode into the string value.
    [Fact]
    public void LegalEscapes_DecodeIntoTheStringValue()
    {
        StructuredFieldParser.TryParseList(@"""a\\b"";r=1", out var items).Should().BeTrue();
        items[0].Value.Text.Should().Be(@"a\b");
    }
}
