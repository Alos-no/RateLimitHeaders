using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

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
/// and the most restrictive one (lowest remaining percentage) is returned.
/// </para>
/// <para>
/// Per the specification, malformed headers are silently ignored (never throws).
/// </para>
/// </remarks>
public static partial class RateLimitHeaderParser
{
    /// <summary>The standard RateLimit header name.</summary>
    public const string RateLimitHeaderName = "RateLimit";

    /// <summary>The standard RateLimit-Policy header name.</summary>
    public const string RateLimitPolicyHeaderName = "RateLimit-Policy";

    // Pattern to match a policy entry starting with quoted policy name
    // Group 1 captures the policy name (without quotes)
    [GeneratedRegex(@"""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PolicyNamePattern();

    // Pattern to extract 'r' (remaining) parameter - order independent, requires preceding semicolon
    [GeneratedRegex(@";\s*r\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemainingPattern();

    // Pattern to extract 't' (reset seconds) parameter - order independent, requires preceding semicolon
    [GeneratedRegex(@";\s*t\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResetSecondsPattern();

    // Pattern to extract 'q' (quota) parameter - order independent, requires preceding semicolon
    [GeneratedRegex(@";\s*q\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotaPattern();

    // Pattern to extract 'w' (window seconds) parameter - order independent, requires preceding semicolon
    [GeneratedRegex(@";\s*w\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowSecondsPattern();

    // Pattern to extract optional pk (partition key) parameter
    [GeneratedRegex(@";?\s*pk\s*=\s*(?:""([^""]+)""|([^\s;,]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartitionKeyPattern();

    // Pattern to extract optional qu (quota unit) parameter
    [GeneratedRegex(@";?\s*qu\s*=\s*(?:""([^""]+)""|([^\s;,]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotaUnitPattern();

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

        return ParseCore(rateLimitValue, rateLimitPolicyValue);
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
    /// Parses RateLimit headers from raw header values (for testing or custom scenarios).
    /// </summary>
    /// <param name="rateLimitHeaderValue">The value of the RateLimit header, or null if not present.</param>
    /// <param name="rateLimitPolicyHeaderValue">The value of the RateLimit-Policy header, or null if not present.</param>
    /// <returns>A <see cref="RateLimitInfo"/> with parsed values, or an invalid instance if parsing fails.</returns>
    public static RateLimitInfo Parse(string? rateLimitHeaderValue, string? rateLimitPolicyHeaderValue)
    {
        return ParseCore(rateLimitHeaderValue, rateLimitPolicyHeaderValue);
    }

    private static bool TryGetHeaderValue(HttpResponseHeaders headers, string headerName, [NotNullWhen(true)] out string? value)
    {
        value = null;

        if (!headers.TryGetValues(headerName, out var values))
        {
            return false;
        }

        // Combine all header values (handles both comma-separated in single value
        // and multiple header instances)
        value = string.Join(",", values.Select(v => v?.Trim()).Where(v => !string.IsNullOrEmpty(v)));
        return !string.IsNullOrEmpty(value);
    }

    private static List<RateLimitEntry> ParseAllRateLimitEntries(string value)
    {
        var entries = new List<RateLimitEntry>();

        // Split by comma to handle multiple policies, but be careful of commas in quoted strings
        var policyEntries = SplitPolicyEntries(value);

        foreach (var entryText in policyEntries)
        {
            // Extract policy name
            var policyMatch = PolicyNamePattern().Match(entryText);
            if (!policyMatch.Success || string.IsNullOrEmpty(policyMatch.Groups[1].Value))
            {
                continue;
            }

            var policyName = policyMatch.Groups[1].Value;

            // Extract remaining (r) parameter - order independent
            var remainingMatch = RemainingPattern().Match(entryText);
            if (!remainingMatch.Success || !int.TryParse(remainingMatch.Groups[1].ValueSpan, out var remaining))
            {
                continue;
            }

            // Extract reset seconds (t) parameter - order independent
            var resetMatch = ResetSecondsPattern().Match(entryText);
            if (!resetMatch.Success || !int.TryParse(resetMatch.Groups[1].ValueSpan, out var resetSeconds))
            {
                continue;
            }

            // Skip entries with negative values
            if (remaining < 0 || resetSeconds < 0)
            {
                continue;
            }

            entries.Add(new RateLimitEntry(policyName, remaining, resetSeconds));
        }

        return entries;
    }

    private static List<RateLimitPolicyEntry> ParseAllRateLimitPolicyEntries(string value)
    {
        var entries = new List<RateLimitPolicyEntry>();

        // Split by comma to handle multiple policies
        var policyEntries = SplitPolicyEntries(value);

        foreach (var entryText in policyEntries)
        {
            // Extract policy name
            var policyMatch = PolicyNamePattern().Match(entryText);
            if (!policyMatch.Success || string.IsNullOrEmpty(policyMatch.Groups[1].Value))
            {
                continue;
            }

            var policyName = policyMatch.Groups[1].Value;

            // Extract quota (q) parameter - order independent
            var quotaMatch = QuotaPattern().Match(entryText);
            if (!quotaMatch.Success || !int.TryParse(quotaMatch.Groups[1].ValueSpan, out var quota))
            {
                continue;
            }

            // Extract window seconds (w) parameter - order independent
            var windowMatch = WindowSecondsPattern().Match(entryText);
            if (!windowMatch.Success || !int.TryParse(windowMatch.Groups[1].ValueSpan, out var windowSeconds))
            {
                continue;
            }

            // Skip entries with negative values
            if (quota < 0 || windowSeconds < 0)
            {
                continue;
            }

            // Parse optional pk (partition key) parameter
            var partitionKey = ExtractOptionalParam(entryText, PartitionKeyPattern());

            // Parse optional qu (quota unit) parameter
            var quotaUnit = ExtractOptionalParam(entryText, QuotaUnitPattern());

            entries.Add(new RateLimitPolicyEntry(
                policyName,
                quota,
                windowSeconds,
                partitionKey,
                quotaUnit));
        }

        return entries;
    }

    /// <summary>
    /// Splits header value by comma, respecting quoted strings.
    /// Uses Span-based processing to minimize allocations.
    /// </summary>
    private static List<string> SplitPolicyEntries(string value)
    {
        var entries = new List<string>();
        var span = value.AsSpan();
        var inQuotes = false;
        var entryStart = 0;

        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                var entrySpan = span[entryStart..i].Trim();
                if (!entrySpan.IsEmpty)
                {
                    entries.Add(entrySpan.ToString());
                }
                entryStart = i + 1;
            }
        }

        // Add the last entry
        var lastEntrySpan = span[entryStart..].Trim();
        if (!lastEntrySpan.IsEmpty)
        {
            entries.Add(lastEntrySpan.ToString());
        }

        return entries;
    }

    /// <summary>
    /// Extracts an optional parameter value from a policy entry substring.
    /// </summary>
    private static string? ExtractOptionalParam(string entrySubstring, Regex pattern)
    {
        var match = pattern.Match(entrySubstring);
        if (!match.Success)
        {
            return null;
        }

        // First capture group is for quoted value, second is for unquoted
        return !string.IsNullOrEmpty(match.Groups[1].Value)
            ? match.Groups[1].Value
            : !string.IsNullOrEmpty(match.Groups[2].Value)
                ? match.Groups[2].Value
                : null;
    }

    /// <summary>
    /// Core parsing logic that matches policy names between RateLimit and RateLimit-Policy headers
    /// and returns the most restrictive policy.
    /// </summary>
    private static RateLimitInfo ParseCore(string? rateLimitValue, string? rateLimitPolicyValue)
    {
        var rateLimitEntries = string.IsNullOrWhiteSpace(rateLimitValue)
            ? []
            : ParseAllRateLimitEntries(rateLimitValue);

        var policyEntries = string.IsNullOrWhiteSpace(rateLimitPolicyValue)
            ? []
            : ParseAllRateLimitPolicyEntries(rateLimitPolicyValue);

        if (rateLimitEntries.Count == 0 && policyEntries.Count == 0)
        {
            return default;
        }

        // Build a dictionary of policy entries for quick lookup by name.
        // Use first occurrence in case of duplicates (per IETF spec: silently handle malformed input).
        var policyDict = new Dictionary<string, RateLimitPolicyEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in policyEntries)
        {
            // TryAdd ignores duplicates, keeping the first occurrence
            policyDict.TryAdd(entry.PolicyName, entry);
        }

        // Find the most restrictive combination by matching policy names
        RateLimitInfo? mostRestrictive = null;
        double lowestPercentage = double.MaxValue;

        foreach (var entry in rateLimitEntries)
        {
            var quota = 0;
            var windowSeconds = 0;
            string? partitionKey = null;
            string? quotaUnit = null;

            // Try to match with policy header by name
            if (policyDict.TryGetValue(entry.PolicyName, out var policyEntry))
            {
                quota = policyEntry.Quota;
                windowSeconds = policyEntry.WindowSeconds;
                partitionKey = policyEntry.PartitionKey;
                quotaUnit = policyEntry.QuotaUnit;
            }

            var info = new RateLimitInfo
            {
                PolicyName = entry.PolicyName,
                Remaining = entry.Remaining,
                ResetSeconds = entry.ResetSeconds,
                Quota = quota,
                WindowSeconds = windowSeconds,
                PartitionKey = partitionKey,
                QuotaUnit = quotaUnit,
                IsValid = true
            };

            // Calculate restrictiveness (lower remaining percentage = more restrictive)
            var percentage = info.GetRemainingPercentage();
            if (percentage < lowestPercentage)
            {
                lowestPercentage = percentage;
                mostRestrictive = info;
            }
        }

        // If we have rate limit entries, return the most restrictive
        if (mostRestrictive.HasValue)
        {
            return mostRestrictive.Value;
        }

        // No rate limit entries but we have policy entries - return the most restrictive policy
        if (policyEntries.Count > 0)
        {
            var policy = policyEntries.OrderBy(p => p.Quota).First();
            return new RateLimitInfo
            {
                PolicyName = policy.PolicyName,
                Quota = policy.Quota,
                WindowSeconds = policy.WindowSeconds,
                PartitionKey = policy.PartitionKey,
                QuotaUnit = policy.QuotaUnit,
                IsValid = true
            };
        }

        return default;
    }

    /// <summary>
    /// Parsed rate limit entry from a single policy.
    /// </summary>
    private readonly record struct RateLimitEntry(string PolicyName, int Remaining, int ResetSeconds);

    /// <summary>
    /// Parsed rate limit policy entry with optional IETF parameters.
    /// </summary>
    private readonly record struct RateLimitPolicyEntry(
        string PolicyName,
        int Quota,
        int WindowSeconds,
        string? PartitionKey,
        string? QuotaUnit);
}
