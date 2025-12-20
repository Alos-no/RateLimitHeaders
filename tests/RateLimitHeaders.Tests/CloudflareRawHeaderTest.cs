using System.Net.Http.Headers;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Tests.Secrets;

namespace RateLimitHeaders.Tests;

/// <summary>
/// Integration test that proves raw Cloudflare headers match parsed output.
/// This test asserts that Cloudflare returns rate limit headers - if they stop
/// returning headers, the test will fail to alert us to API changes.
/// </summary>
public class CloudflareRawHeaderTest : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _isConfigured;

    public CloudflareRawHeaderTest()
    {
        _isConfigured = TestSecretsConfiguration.IsCloudflareConfigured;
        _client = new HttpClient
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/")
        };

        if (_isConfigured)
        {
            _client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", TestSecretsConfiguration.CloudflareSettings.ApiToken);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ProveRateLimitHeaderParsing()
    {
        Assert.True(_isConfigured, "Cloudflare API token not configured. Run scripts/setup-test-secrets.ps1");

        // Make request to Cloudflare API
        var response = await _client.GetAsync("zones");

        Console.WriteLine("========================================");
        Console.WriteLine("RAW HTTP RESPONSE FROM CLOUDFLARE API");
        Console.WriteLine("========================================");
        Console.WriteLine($"Status Code: {(int)response.StatusCode} {response.StatusCode}");
        Console.WriteLine();

        // Print ALL raw headers
        Console.WriteLine("--- ALL RESPONSE HEADERS (RAW) ---");
        foreach (var header in response.Headers.OrderBy(h => h.Key))
        {
            foreach (var value in header.Value)
            {
                Console.WriteLine($"{header.Key}: {value}");
            }
        }
        Console.WriteLine();

        // Extract the specific rate limit headers
        string? rawRateLimit = null;
        string? rawRateLimitPolicy = null;

        if (response.Headers.TryGetValues("RateLimit", out var rateLimitValues))
        {
            rawRateLimit = string.Join(", ", rateLimitValues);
        }
        if (response.Headers.TryGetValues("RateLimit-Policy", out var policyValues))
        {
            rawRateLimitPolicy = string.Join(", ", policyValues);
        }

        Console.WriteLine("--- RATE LIMIT HEADERS (EXTRACTED) ---");
        Console.WriteLine($"RateLimit: {rawRateLimit ?? "(not present)"}");
        Console.WriteLine($"RateLimit-Policy: {rawRateLimitPolicy ?? "(not present)"}");
        Console.WriteLine();

        // Now parse with our library
        Console.WriteLine("========================================");
        Console.WriteLine("PARSED OUTPUT FROM OUR LIBRARY");
        Console.WriteLine("========================================");

        // Assert - Cloudflare should return rate limit headers
        rawRateLimit.Should().NotBeNull(
            "Cloudflare API should return RateLimit header. If this fails, the API behavior may have changed.");

        var parsed = RateLimitHeaderParser.TryParse(response, out var info);

        Console.WriteLine($"TryParse returned: {parsed}");
        Console.WriteLine($"RateLimitInfo.IsValid: {info.IsValid}");
        Console.WriteLine();

        // Assert - Parser should successfully parse headers
        parsed.Should().BeTrue("Parser should successfully parse Cloudflare rate limit headers");
        info.IsValid.Should().BeTrue("Parsed info should be valid");

        Console.WriteLine("--- PARSED VALUES ---");
        Console.WriteLine($"PolicyName: \"{info.PolicyName}\"");
        Console.WriteLine($"Remaining: {info.Remaining}");
        Console.WriteLine($"ResetSeconds: {info.ResetSeconds}");
        Console.WriteLine($"Quota: {info.Quota}");
        Console.WriteLine($"WindowSeconds: {info.WindowSeconds}");
        Console.WriteLine($"GetRemainingPercentage(): {info.GetRemainingPercentage():P2}");
        Console.WriteLine($"IsQuotaLow(0.1): {info.IsQuotaLow(0.1)}");
        Console.WriteLine($"ToString(): {info}");
        Console.WriteLine();

        // Prove the values match by extracting from raw header
        Console.WriteLine("========================================");
        Console.WriteLine("VERIFICATION: RAW vs PARSED");
        Console.WriteLine("========================================");

        // Parse expected from raw: "default";r=1199;t=1
        Console.WriteLine($"Raw RateLimit header: {rawRateLimit}");
        Console.WriteLine($"  Expected format: \"<policy>\";r=<remaining>;t=<reset>");

        // Extract values manually for comparison
        var parts = rawRateLimit!.Split(';');
        var policyPart = parts[0].Trim().Trim('"');
        var remainingPart = parts.FirstOrDefault(p => p.Trim().StartsWith("r=", StringComparison.Ordinal))?.Split('=')[1];
        var resetPart = parts.FirstOrDefault(p => p.Trim().StartsWith("t=", StringComparison.Ordinal))?.Split('=')[1];

        Console.WriteLine($"  Manual extraction: policy=\"{policyPart}\", r={remainingPart}, t={resetPart}");
        Console.WriteLine($"  Library parsed:    policy=\"{info.PolicyName}\", r={info.Remaining}, t={info.ResetSeconds}");

        if (int.TryParse(remainingPart, out var expectedRemaining))
        {
            info.Remaining.Should().Be(expectedRemaining, "Remaining should match raw header");
        }
        if (int.TryParse(resetPart, out var expectedReset))
        {
            info.ResetSeconds.Should().Be(expectedReset, "ResetSeconds should match raw header");
        }

        if (rawRateLimitPolicy != null)
        {
            Console.WriteLine();
            Console.WriteLine($"Raw RateLimit-Policy header: {rawRateLimitPolicy}");
            Console.WriteLine($"  Expected format: \"<policy>\";q=<quota>;w=<window>");

            var policyParts = rawRateLimitPolicy.Split(';');
            var quotaPart = policyParts.FirstOrDefault(p => p.Trim().StartsWith("q=", StringComparison.Ordinal))?.Split('=')[1];
            var windowPart = policyParts.FirstOrDefault(p => p.Trim().StartsWith("w=", StringComparison.Ordinal))?.Split('=')[1];

            Console.WriteLine($"  Manual extraction: q={quotaPart}, w={windowPart}");
            Console.WriteLine($"  Library parsed:    q={info.Quota}, w={info.WindowSeconds}");

            if (int.TryParse(quotaPart, out var expectedQuota))
            {
                info.Quota.Should().Be(expectedQuota, "Quota should match raw header");
            }
            if (int.TryParse(windowPart, out var expectedWindow))
            {
                info.WindowSeconds.Should().Be(expectedWindow, "WindowSeconds should match raw header");
            }
        }

        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine("VERIFICATION PASSED - All values match!");
        Console.WriteLine("========================================");
    }
}
