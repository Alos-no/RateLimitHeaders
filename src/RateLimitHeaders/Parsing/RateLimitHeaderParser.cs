using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using RateLimitHeaders.Internal;

namespace RateLimitHeaders.Parsing;

/// <summary>
/// Parses IETF RateLimit headers (draft-ietf-httpapi-ratelimit-headers-10) from HTTP responses.
/// </summary>
/// <remarks>
/// <para>
/// This parser supports the IETF standard format:
/// <list type="bullet">
/// <item><c>RateLimit: "policy";r=remaining;t=reset_seconds</c></item>
/// <item><c>RateLimit-Policy: "policy";q=quota;w=window_seconds</c></item>
/// </list>
/// </para>
/// <para>
/// Multiple comma-separated policies are supported per the spec:
/// <code>RateLimit: "burst";r=50;t=30,"daily";r=900;t=43200</code>
/// When multiple policies are present, policy names are matched between headers
/// and the most restrictive one is returned; <see cref="ParseAll(string?, string?)"/>
/// returns every described policy ordered most restrictive first.
/// </para>
/// <para>
/// Parsing is strict per RFC 9651 (Decision 1 in PLAN-audit-fixes.md): a field value with
/// any malformed member is discarded whole, with no partial result, so malformed hostile
/// input can never become trusted throttling state. The two header fields are discarded
/// independently: a malformed RateLimit-Policy value does not void a well-formed RateLimit
/// value, and vice versa. The parser never throws on malformed input.
/// </para>
/// <para>
/// Field-specific validity rules on top of the generic syntax: <c>r</c>, <c>t</c>, <c>q</c>,
/// and <c>w</c> must be non-negative structured-field integers (<c>w</c> strictly positive);
/// <c>r</c> is required on RateLimit entries and <c>q</c> on RateLimit-Policy entries, while
/// <c>t</c> and <c>w</c> are optional per the draft; <c>pk</c> must be a byte sequence;
/// unknown parameters are ignored. Duplicate parameters and duplicate policy names resolve
/// last-wins.
/// </para>
/// </remarks>
public static class RateLimitHeaderParser
{
    /// <summary>The standard RateLimit header name.</summary>
    public const string RateLimitHeaderName = "RateLimit";

    /// <summary>The standard RateLimit-Policy header name.</summary>
    public const string RateLimitPolicyHeaderName = "RateLimit-Policy";

    /// <summary>
    /// Parses RateLimit headers from an HTTP response.
    /// </summary>
    /// <param name="response">The HTTP response to parse headers from.</param>
    /// <returns>A <see cref="RateLimitInfo"/> with parsed values, or an invalid instance if parsing fails.</returns>
    public static RateLimitInfo Parse(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return Parse(response.Headers);
    }

    /// <summary>
    /// Parses RateLimit headers from HTTP response headers.
    /// </summary>
    /// <param name="headers">The HTTP response headers to parse.</param>
    /// <returns>A <see cref="RateLimitInfo"/> with parsed values, or an invalid instance if parsing fails.</returns>
    public static RateLimitInfo Parse(HttpResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        TryGetHeaderValue(headers, RateLimitHeaderName, out var rateLimitValue);
        TryGetHeaderValue(headers, RateLimitPolicyHeaderName, out var rateLimitPolicyValue);

        return Parse(rateLimitValue, rateLimitPolicyValue);
    }

    /// <summary>
    /// Attempts to parse RateLimit headers from an HTTP response.
    /// </summary>
    /// <param name="response">The HTTP response to parse headers from.</param>
    /// <param name="rateLimitInfo">When successful, contains the parsed rate limit information.</param>
    /// <returns><c>true</c> if at least one header was successfully parsed; otherwise, <c>false</c>.</returns>
    public static bool TryParse(HttpResponseMessage response, out RateLimitInfo rateLimitInfo)
    {
        ArgumentNullException.ThrowIfNull(response);
        rateLimitInfo = Parse(response);
        return rateLimitInfo.IsValid;
    }

    /// <summary>
    /// Attempts to parse RateLimit headers from HTTP response headers.
    /// </summary>
    /// <param name="headers">The HTTP response headers to parse.</param>
    /// <param name="rateLimitInfo">When successful, contains the parsed rate limit information.</param>
    /// <returns><c>true</c> if at least one header was successfully parsed; otherwise, <c>false</c>.</returns>
    public static bool TryParse(HttpResponseHeaders headers, out RateLimitInfo rateLimitInfo)
    {
        ArgumentNullException.ThrowIfNull(headers);
        rateLimitInfo = Parse(headers);
        return rateLimitInfo.IsValid;
    }

    /// <summary>
    /// Parses RateLimit headers from raw header values (for testing or custom scenarios)
    /// and returns the most restrictive policy.
    /// </summary>
    /// <param name="rateLimitHeaderValue">The value of the RateLimit header, or null if not present.</param>
    /// <param name="rateLimitPolicyHeaderValue">The value of the RateLimit-Policy header, or null if not present.</param>
    /// <returns>A <see cref="RateLimitInfo"/> with parsed values, or an invalid instance if parsing fails.</returns>
    public static RateLimitInfo Parse(string? rateLimitHeaderValue, string? rateLimitPolicyHeaderValue)
    {
        var all = ParseAll(rateLimitHeaderValue, rateLimitPolicyHeaderValue);
        return all.Count > 0 ? all[0] : default;
    }

    /// <summary>
    /// Parses every policy the response describes, from an HTTP response, ordered most
    /// restrictive first (the comparator in <see cref="CompareRestrictiveness"/>).
    /// </summary>
    /// <param name="response">The HTTP response to parse headers from.</param>
    /// <returns>All parsed policies, most restrictive first; empty when nothing parsed.</returns>
    public static IReadOnlyList<RateLimitInfo> ParseAll(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        TryGetHeaderValue(response.Headers, RateLimitHeaderName, out var rateLimitValue);
        TryGetHeaderValue(response.Headers, RateLimitPolicyHeaderName, out var rateLimitPolicyValue);

        return ParseAll(rateLimitValue, rateLimitPolicyValue);
    }

    /// <summary>
    /// Parses every policy the two header values describe, ordered most restrictive first.
    /// Policies present only in the RateLimit-Policy field (no matching RateLimit entry)
    /// are included as quota-only entries.
    /// </summary>
    /// <param name="rateLimitHeaderValue">The value of the RateLimit header, or null if not present.</param>
    /// <param name="rateLimitPolicyHeaderValue">The value of the RateLimit-Policy header, or null if not present.</param>
    /// <returns>All parsed policies, most restrictive first; empty when nothing parsed.</returns>
    public static IReadOnlyList<RateLimitInfo> ParseAll(string? rateLimitHeaderValue, string? rateLimitPolicyHeaderValue)
    {
        // The two fields are parsed and discarded independently: a malformed value in one
        // never voids the other
        var rateLimitEntries = ParseRateLimitField(rateLimitHeaderValue) ?? [];
        var policyEntries = ParsePolicyField(rateLimitPolicyHeaderValue) ?? [];

        if (rateLimitEntries.Count == 0 && policyEntries.Count == 0)
        {
            return [];
        }

        // Duplicate policy names resolve last-wins in both fields, matching the duplicate
        // parameter rule; every later loop iterates the deduplicated collections so a
        // repeated name can never emit two results or let a superseded value win
        var policiesByName = new Dictionary<string, PolicyEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in policyEntries)
        {
            policiesByName[policy.PolicyName] = policy;
        }

        var rateLimitByName = new Dictionary<string, RateLimitEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in rateLimitEntries)
        {
            rateLimitByName[entry.PolicyName] = entry;
        }

        var results = new List<RateLimitInfo>(rateLimitByName.Count + policiesByName.Count);
        var consumedPolicyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in rateLimitByName.Values)
        {
            var info = new RateLimitInfo
            {
                PolicyName = entry.PolicyName,
                Remaining = entry.Remaining,
                IsValid = true,
            };

            if (entry.ResetSeconds is long reset)
            {
                info = info with { ResetSeconds = reset };
            }

            if (policiesByName.TryGetValue(entry.PolicyName, out var policy))
            {
                consumedPolicyNames.Add(entry.PolicyName);
                info = info with { Quota = policy.Quota };
                if (policy.WindowSeconds is long window)
                {
                    info = info with { WindowSeconds = window };
                }

                // The RateLimit field's own pk/qu win over the policy field's (PARSE26);
                // an empty value on the RateLimit entry does not suppress a real one on
                // the policy entry
                info = info with
                {
                    PartitionKey = string.IsNullOrEmpty(entry.PartitionKey) ? policy.PartitionKey : entry.PartitionKey,
                    QuotaUnit = string.IsNullOrEmpty(entry.QuotaUnit) ? policy.QuotaUnit : entry.QuotaUnit,
                };
            }
            else
            {
                info = info with { PartitionKey = entry.PartitionKey, QuotaUnit = entry.QuotaUnit };
            }

            results.Add(info);
        }

        foreach (var policy in policiesByName.Values)
        {
            if (consumedPolicyNames.Contains(policy.PolicyName))
            {
                continue;
            }

            var info = new RateLimitInfo
            {
                PolicyName = policy.PolicyName,
                Quota = policy.Quota,
                PartitionKey = policy.PartitionKey,
                QuotaUnit = policy.QuotaUnit,
                IsValid = true,
            };

            if (policy.WindowSeconds is long window)
            {
                info = info with { WindowSeconds = window };
            }

            results.Add(info);
        }

        results.Sort(CompareRestrictiveness);
        return results;
    }

    /// <summary>
    /// Orders two policies by restrictiveness, most restrictive first; the first differing
    /// rung wins (the T5 band of PLAN-audit-fixes.md):
    /// (1) an exhausted entry beats a non-exhausted one;
    /// (2) both exhausted: the later reset moment wins;
    /// (3) a known remaining count beats an unknown one, and the lower count wins;
    /// (4) equal counts: the lower remaining fraction wins, and a known fraction beats none;
    /// (5) still equal: the longer reset horizon wins;
    /// (6) still equal: a known, smaller quota wins (this is what orders quota-only
    /// entries, which none of the earlier rungs distinguish);
    /// (7) still equal: ordinal comparison of the policy names, purely for determinism.
    /// </summary>
    private static int CompareRestrictiveness(RateLimitInfo left, RateLimitInfo right)
    {
        // Rung 1: exhaustion dominates
        if (left.IsExhausted != right.IsExhausted)
        {
            return left.IsExhausted ? -1 : 1;
        }

        // Rung 2: both exhausted, the later reset moment is the harder stop
        if (left.IsExhausted && right.IsExhausted && left.ResetSeconds != right.ResetSeconds)
        {
            return right.ResetSeconds.CompareTo(left.ResetSeconds);
        }

        // Rung 3: a known remaining count is actionable restriction; lower is tighter
        if (left.HasRemaining != right.HasRemaining)
        {
            return left.HasRemaining ? -1 : 1;
        }

        if (left.HasRemaining && left.Remaining != right.Remaining)
        {
            return left.Remaining.CompareTo(right.Remaining);
        }

        // Rung 4: same count, the lower share of quota is tighter; a known share beats none
        var leftFraction = left.RemainingFraction;
        var rightFraction = right.RemainingFraction;
        if (leftFraction.HasValue != rightFraction.HasValue)
        {
            return leftFraction.HasValue ? -1 : 1;
        }

        if (leftFraction.HasValue && rightFraction.HasValue && leftFraction.Value != rightFraction.Value)
        {
            return leftFraction.Value.CompareTo(rightFraction.Value);
        }

        // Rung 5: the longer horizon binds the caller longer
        if (left.ResetSeconds != right.ResetSeconds)
        {
            return right.ResetSeconds.CompareTo(left.ResetSeconds);
        }

        // Rung 6: for quota-only entries nothing above differs; the smaller advertised
        // quota is the tighter limit, and a known quota beats an unknown one
        if (left.HasQuota != right.HasQuota)
        {
            return left.HasQuota ? -1 : 1;
        }

        if (left.HasQuota && left.Quota != right.Quota)
        {
            return left.Quota.CompareTo(right.Quota);
        }

        // Rung 7: determinism only
        return string.CompareOrdinal(left.PolicyName, right.PolicyName);
    }

    private static bool TryGetHeaderValue(HttpResponseHeaders headers, string headerName, [NotNullWhen(true)] out string? value)
    {
        value = null;

        if (!headers.TryGetValues(headerName, out var values))
        {
            return false;
        }

        // Combine repeated header lines with a comma, verbatim, per RFC 9110 field
        // combination. An empty line among real values produces an empty list member,
        // which strict parsing then rejects; filtering empty values here would salvage
        // a malformed field.
        var combined = string.Join(",", values);
        if (string.IsNullOrWhiteSpace(combined))
        {
            return false;
        }

        value = combined;
        return true;
    }

    /// <summary>
    /// Parses the RateLimit field value. Returns null when the value is absent or malformed
    /// (the whole field is discarded; entries are never salvaged).
    /// </summary>
    private static List<RateLimitEntry>? ParseRateLimitField(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!StructuredFieldParser.TryParseList(value, out var items))
        {
            return null;
        }

        var entries = new List<RateLimitEntry>(items.Count);
        foreach (var item in items)
        {
            // The policy name must be a non-empty quoted string
            if (item.Value.Kind != StructuredFieldValueKind.String || string.IsNullOrEmpty(item.Value.Text))
            {
                return null;
            }

            // r is required on a RateLimit entry
            if (!TryReadCount(item, "r", out var remaining) || remaining is null)
            {
                return null;
            }

            if (!TryReadCount(item, "t", out var resetSeconds))
            {
                return null;
            }

            if (!TryReadPartitionKey(item, out var partitionKey) || !TryReadQuotaUnit(item, out var quotaUnit))
            {
                return null;
            }

            entries.Add(new RateLimitEntry(item.Value.Text, remaining.Value, resetSeconds, partitionKey, quotaUnit));
        }

        return entries;
    }

    /// <summary>
    /// Parses the RateLimit-Policy field value. Returns null when the value is absent or
    /// malformed (the whole field is discarded; entries are never salvaged).
    /// </summary>
    private static List<PolicyEntry>? ParsePolicyField(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!StructuredFieldParser.TryParseList(value, out var items))
        {
            return null;
        }

        var entries = new List<PolicyEntry>(items.Count);
        foreach (var item in items)
        {
            if (item.Value.Kind != StructuredFieldValueKind.String || string.IsNullOrEmpty(item.Value.Text))
            {
                return null;
            }

            // q is required on a policy entry
            if (!TryReadCount(item, "q", out var quota) || quota is null)
            {
                return null;
            }

            // w is optional, but when present the draft requires a positive window
            if (!TryReadCount(item, "w", out var windowSeconds) || windowSeconds is 0)
            {
                return null;
            }

            if (!TryReadPartitionKey(item, out var partitionKey) || !TryReadQuotaUnit(item, out var quotaUnit))
            {
                return null;
            }

            entries.Add(new PolicyEntry(item.Value.Text, quota.Value, windowSeconds, partitionKey, quotaUnit));
        }

        return entries;
    }

    /// <summary>
    /// Reads one of the counting parameters (r, t, q, w). Returns false when the parameter
    /// is present but is not a non-negative structured-field integer, which voids the field;
    /// an absent parameter reads as null with true.
    /// </summary>
    private static bool TryReadCount(StructuredFieldItem item, string key, out long? count)
    {
        count = null;

        if (!item.TryGetParameter(key, out var value))
        {
            return true;
        }

        if (value.Kind != StructuredFieldValueKind.Integer || value.IntegerValue < 0)
        {
            return false;
        }

        count = value.IntegerValue;
        return true;
    }

    /// <summary>
    /// Reads the partition key (pk), which must be a byte sequence when present. The decoded
    /// text follows the byte-sequence rule: clean UTF-8 without control characters, otherwise
    /// the base64 text.
    /// </summary>
    private static bool TryReadPartitionKey(StructuredFieldItem item, out string? partitionKey)
    {
        partitionKey = null;

        if (!item.TryGetParameter("pk", out var value))
        {
            return true;
        }

        if (value.Kind != StructuredFieldValueKind.ByteSequence)
        {
            return false;
        }

        partitionKey = value.Text;
        return true;
    }

    /// <summary>
    /// Reads the quota unit (qu), which must be a string or token when present.
    /// </summary>
    private static bool TryReadQuotaUnit(StructuredFieldItem item, out string? quotaUnit)
    {
        quotaUnit = null;

        if (!item.TryGetParameter("qu", out var value))
        {
            return true;
        }

        if (value.Kind is not (StructuredFieldValueKind.String or StructuredFieldValueKind.Token))
        {
            return false;
        }

        quotaUnit = value.Text;
        return true;
    }

    /// <summary>
    /// Parsed rate limit entry from a single policy. Null means the parameter was absent.
    /// </summary>
    private readonly record struct RateLimitEntry(
        string PolicyName,
        long Remaining,
        long? ResetSeconds,
        string? PartitionKey,
        string? QuotaUnit);

    /// <summary>
    /// Parsed rate limit policy entry with optional IETF parameters. Null means the parameter was absent.
    /// </summary>
    private readonly record struct PolicyEntry(
        string PolicyName,
        long Quota,
        long? WindowSeconds,
        string? PartitionKey,
        string? QuotaUnit);
}
