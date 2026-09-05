using Microsoft.Extensions.Logging.Abstractions;
using RateLimitHeaders.Http;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Tests.Fixtures;

namespace RateLimitHeaders.Tests.AuditFixes;

/// <summary>
/// Scenarios DI16-DI20 from PLAN-audit-fixes.md (task T21): the default state key carries
/// scheme, host, and port so different ports and schemes of one host stop sharing an entry
/// (finding AUD-29 in TRACKER-adversarial-audit.md, the adversarial-audit findings ledger);
/// a relative request URI and a blank extractor result fall back instead of throwing or
/// creating a whitespace key.
/// </summary>
public class StateKeyShapeTests
{
    // DI16: the default key carries scheme and port.
    // Red baseline today: the key is the bare hostname, api.example.com.
    [Fact]
    public void DefaultKey_CarriesSchemeAndPort()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v1/users");

        var key = RateLimitOptionsHelper.GetStateKey(request, trackPerEndpoint: true, customExtractor: null);

        key.Should().Be("https://api.example.com:443");
    }

    // DI18: HTTP and HTTPS to one host are tracked separately.
    [Fact]
    public void HttpAndHttps_YieldDistinctKeys()
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Get, "http://api.example.com/x");
        var httpsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/x");

        RateLimitOptionsHelper.GetStateKey(httpRequest, true, null).Should().Be("http://api.example.com:80");
        RateLimitOptionsHelper.GetStateKey(httpsRequest, true, null).Should().Be("https://api.example.com:443");
    }

    // DI19: a relative request URI yields the fallback key instead of throwing.
    // Red baseline today: Uri.Host throws InvalidOperationException on a relative URI.
    [Fact]
    public void RelativeUri_YieldsTheDefaultKey()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/test", UriKind.Relative));

        var key = RateLimitOptionsHelper.GetStateKey(request, true, null);

        key.Should().Be("default");
    }

    // DI20: a blank extractor result falls back to the global key.
    // Red baseline today: the whitespace string becomes a key of its own.
    [Fact]
    public void WhitespaceExtractorResult_FallsBackToTheGlobalKey()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/x");

        var key = RateLimitOptionsHelper.GetStateKey(request, true, _ => "   ");

        key.Should().Be("global");
    }

    // DI17: different ports of one host stop sharing an entry (the AUD-29 scenario).
    // Red baseline today: both writes land under the key "localhost" and the second erases the first.
    [Fact]
    public async Task DifferentPorts_KeepSeparateEntries()
    {
        var tracker = new RateLimitStateTracker();

        var mockA = new MockHttpHandler();
        mockA.QueueRateLimitResponse(remaining: 2, resetSeconds: 30, quota: 100, windowSeconds: 60);
        var handlerA = new RateLimitAwareHandler(
            new RateLimitAwareOptions { EnableProactiveThrottling = false, TrackStatePerEndpoint = true },
            tracker,
            NullLogger.Instance)
        {
            InnerHandler = mockA
        };

        var mockB = new MockHttpHandler();
        mockB.QueueRateLimitResponse(remaining: 90, resetSeconds: 30, quota: 100, windowSeconds: 60);
        var handlerB = new RateLimitAwareHandler(
            new RateLimitAwareOptions { EnableProactiveThrottling = false, TrackStatePerEndpoint = true },
            tracker,
            NullLogger.Instance)
        {
            InnerHandler = mockB
        };

        await new HttpClient(handlerA).GetAsync("http://localhost:8080/api/x");
        await new HttpClient(handlerB).GetAsync("http://localhost:9090/api/x");

        tracker.GetRateLimitInfo("http://localhost:8080").Remaining.Should().Be(2);
        tracker.GetRateLimitInfo("http://localhost:9090").Remaining.Should().Be(90);
        tracker.GetRateLimitInfo("localhost").IsValid.Should().BeFalse("the old host-only key must no longer be written");
    }
}
