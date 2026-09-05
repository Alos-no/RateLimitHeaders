using RateLimitHeaders.Parsing;

namespace RateLimitHeaders.Tests.AuditFixes;

/// <summary>
/// Scenarios PARSE16-PARSE32, PARSE42-PARSE45, and PARSE48 from PLAN-audit-fixes.md
/// (tasks T4 and T5): <c>RateLimitHeaderParser</c> rebuilt on the strict structured-field
/// parser with the field-specific validity rules of draft-ietf-httpapi-ratelimit-headers-10
/// (optional t and w, required non-negative integer r and q, positive w, byte-sequence pk),
/// and most-restrictive selection by the six-rung comparator the T5 band of the plan defines,
/// replacing the ordering by remaining percentage that ranked every unknown-quota entry as
/// fully healthy (finding AUD-08 in TRACKER-adversarial-audit.md, the audit findings ledger).
/// </summary>
public class ParserRewriteTests
{
    // PARSE16: the optional t parameter is no longer required.
    // Red baseline today: the draft's own minimal legal shape parses as invalid.
    [Fact]
    public void OptionalReset_IsNoLongerRequired()
    {
        var info = RateLimitHeaderParser.Parse(@"""default"";r=0", null);

        info.IsValid.Should().BeTrue();
        info.PolicyName.Should().Be("default");
        info.HasRemaining.Should().BeTrue();
        info.Remaining.Should().Be(0);
        info.IsExhausted.Should().BeTrue();
        info.HasResetSeconds.Should().BeFalse();
    }

    // PARSE17: the optional w parameter is no longer required, so the quota survives.
    [Fact]
    public void OptionalWindow_IsNoLongerRequired()
    {
        var info = RateLimitHeaderParser.Parse(@"""default"";r=5;t=30", @"""default"";q=100");

        info.IsValid.Should().BeTrue();
        info.Remaining.Should().Be(5);
        info.ResetSeconds.Should().Be(30);
        info.HasQuota.Should().BeTrue();
        info.Quota.Should().Be(100);
        info.HasWindowSeconds.Should().BeFalse();
    }

    // PARSE18: the injection from the audit is closed end to end.
    // Red baseline today: Remaining 99999, ResetSeconds 0 read out of the quoted policy name.
    [Fact]
    public void EscapedQuoteInjection_IsClosed()
    {
        var info = RateLimitHeaderParser.Parse(@"""pol\"";r=99999;t=0"";r=1;t=3600", null);

        info.IsValid.Should().BeTrue();
        info.Remaining.Should().Be(1);
        info.ResetSeconds.Should().Be(3600);
    }

    // PARSE19: a field with a malformed member is discarded whole (Decision 1).
    // Red baseline today: the "valid" entry is salvaged with Remaining 50.
    [Fact]
    public void MalformedMember_DiscardsTheWholeField()
    {
        var info = RateLimitHeaderParser.Parse(@"""valid"";r=50;t=30,malformed-entry", null);

        info.IsValid.Should().BeFalse();
    }

    // PARSE20: discarding one malformed field keeps the other field usable. (pin)
    [Fact]
    public void PerFieldDiscard_KeepsTheOtherFieldUsable()
    {
        var info = RateLimitHeaderParser.Parse(@"""api"";r=50;t=30", @"""api"";q=abc;w=60");

        info.IsValid.Should().BeTrue();
        info.Remaining.Should().Be(50);
        info.ResetSeconds.Should().Be(30);
        info.HasQuota.Should().BeFalse();
    }

    // PARSE21: duplicate parameters take the last value, per RFC 9651.
    [Fact]
    public void DuplicateParameters_TakeTheLastValue()
    {
        var info = RateLimitHeaderParser.Parse(@"""default"";r=5;r=1;t=30", null);

        info.Remaining.Should().Be(1);
    }

    // PARSE22: the partition key is read from the RateLimit field.
    [Fact]
    public void PartitionKey_IsReadFromTheRateLimitField()
    {
        var info = RateLimitHeaderParser.Parse(@"""default"";r=5;t=30;pk=:dGVuYW50LTEyMw==:", null);

        info.PartitionKey.Should().Be("tenant-123");
    }

    // PARSE23: a byte-sequence partition key is decoded, not surfaced with delimiters.
    [Fact]
    public void PartitionKey_IsDecodedNotDelimited()
    {
        var info = RateLimitHeaderParser.Parse(@"""api"";r=50;t=30", @"""api"";q=100;w=60;pk=:b3JnLTc4OQ==:");

        info.PartitionKey.Should().Be("org-789");
    }

    // PARSE24: an unrelated parameter ending in pk is not the partition key.
    [Fact]
    public void UnrelatedKeyEndingInPk_IsNotThePartitionKey()
    {
        var info = RateLimitHeaderParser.Parse(@"""api"";r=50;t=30", @"""api"";q=100;w=60;spk=3");

        info.PartitionKey.Should().BeNull();
    }

    // PARSE25: quotas above 2^31 parse and flow through.
    [Fact]
    public void QuotasAboveInt32_ParseAndFlowThrough()
    {
        var info = RateLimitHeaderParser.Parse(
            @"""bytes"";r=4000000000;t=3600",
            @"""bytes"";q=5000000000;w=86400;qu=""content-bytes""");

        info.IsValid.Should().BeTrue();
        info.Remaining.Should().Be(4_000_000_000);
        info.Quota.Should().Be(5_000_000_000);
        info.QuotaUnit.Should().Be("content-bytes");
        info.RemainingFraction.Should().Be(0.8);
    }

    // PARSE26: when both fields carry pk for one policy, the RateLimit field's value wins.
    [Fact]
    public void PartitionKey_RateLimitFieldWins()
    {
        var info = RateLimitHeaderParser.Parse(
            @"""default"";r=5;t=30;pk=:cmF0ZQ==:",
            @"""default"";q=100;w=60;pk=:cG9saWN5:");

        info.PartitionKey.Should().Be("rate");
        info.Quota.Should().Be(100);
    }

    // PARSE27: policy-name matching between the two fields stays case-insensitive. (pin)
    [Fact]
    public void PolicyNameMatching_StaysCaseInsensitive()
    {
        var info = RateLimitHeaderParser.Parse(@"""API"";r=1;t=30", @"""api"";q=100;w=60");

        info.IsValid.Should().BeTrue();
        info.Quota.Should().Be(100);
    }

    // PARSE42: a negative count voids the field (the guard the old regexes gave for free).
    [Fact]
    public void NegativeCount_VoidsTheField()
    {
        RateLimitHeaderParser.Parse(@"""default"";r=-1;t=30", null).IsValid.Should().BeFalse();
    }

    // PARSE43: a decimal count voids the field.
    [Fact]
    public void DecimalCount_VoidsTheField()
    {
        RateLimitHeaderParser.Parse(@"""default"";r=1.5;t=30", null).IsValid.Should().BeFalse();
    }

    // PARSE44: a zero window voids the policy field (draft-10 requires a positive window).
    [Fact]
    public void ZeroWindow_VoidsThePolicyField()
    {
        var info = RateLimitHeaderParser.Parse(@"""api"";r=5;t=30", @"""api"";q=100;w=0");

        info.IsValid.Should().BeTrue();
        info.HasQuota.Should().BeFalse();
    }

    // PARSE45: partition-key bytes that are not clean text stay in base64 form, so no
    // control character reaches logs through ToString.
    [Fact]
    public void HostilePartitionKeyBytes_StayInBase64Form()
    {
        var info = RateLimitHeaderParser.Parse(@"""a"";r=5;t=30;pk=:DQpbaG9zdGlsZV0=:", null);

        info.PartitionKey.Should().Be("DQpbaG9zdGlsZV0=");
        info.PartitionKey.Should().NotContain("\r").And.NotContain("\n");
    }

    // PARSE28: an exhausted policy wins even when listed second with no quotas known
    // (comparator rung 1). Red baseline today: the healthy "burst" entry hides "daily".
    [Fact]
    public void ExhaustedPolicy_WinsRegardlessOfListingOrder()
    {
        var info = RateLimitHeaderParser.Parse(@"""burst"";r=50;t=30,""daily"";r=0;t=43200", null);

        info.PolicyName.Should().Be("daily");
        info.Remaining.Should().Be(0);
        info.ResetSeconds.Should().Be(43200);
    }

    // PARSE29: the selection is order-independent.
    [Fact]
    public void Selection_IsOrderIndependent()
    {
        var info = RateLimitHeaderParser.Parse(@"""daily"";r=0;t=43200,""burst"";r=50;t=30", null);

        info.PolicyName.Should().Be("daily");
    }

    // PARSE30: a tie on remaining count resolves by the longer reset horizon (rung 5).
    [Fact]
    public void TieOnRemaining_ResolvesByLongerResetHorizon()
    {
        var info = RateLimitHeaderParser.Parse(@"""a"";r=5;t=30,""b"";r=5;t=3600", null);

        info.PolicyName.Should().Be("b");
        info.ResetSeconds.Should().Be(3600);
    }

    // PARSE31: equal counts with known quotas resolve by the lower remaining fraction (rung 4). (pin)
    [Fact]
    public void EqualCounts_ResolveByLowerFraction()
    {
        var info = RateLimitHeaderParser.Parse(
            @"""a"";r=5;t=60,""b"";r=5;t=60",
            @"""a"";q=10;w=60,""b"";q=1000;w=60");

        info.PolicyName.Should().Be("b");
    }

    // PARSE48: a low count with no quota beats a healthy fraction with one (rung 3, the
    // exact AUD-08 shape). Red baseline today: b's unknown quota reads as 100% healthy.
    [Fact]
    public void LowCountWithoutQuota_BeatsHealthyFractionWithOne()
    {
        var info = RateLimitHeaderParser.Parse(
            @"""a"";r=5;t=60,""b"";r=1;t=60",
            @"""a"";q=10;w=60");

        info.PolicyName.Should().Be("b");
    }

    // PARSE32: every policy the server described is retrievable, ordered most restrictive first.
    [Fact]
    public void ParseAll_ReturnsEveryPolicyMostRestrictiveFirst()
    {
        var withRateLimitField = RateLimitHeaderParser.ParseAll(@"""burst"";r=50;t=30,""daily"";r=0;t=43200", null);
        withRateLimitField.Select(i => i.PolicyName).Should().Equal("daily", "burst");
        withRateLimitField[0].Remaining.Should().Be(0);
        withRateLimitField[1].Remaining.Should().Be(50);

        var policyFieldOnly = RateLimitHeaderParser.ParseAll(null, @"""burst"";q=100;w=60,""daily"";q=1000;w=86400");
        policyFieldOnly.Select(i => i.PolicyName).Should().Equal("burst", "daily");
        policyFieldOnly[0].Quota.Should().Be(100);
        policyFieldOnly[1].Quota.Should().Be(1000);
    }
}
