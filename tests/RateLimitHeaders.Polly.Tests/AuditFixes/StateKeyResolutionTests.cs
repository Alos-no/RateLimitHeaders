using System.Net;
using Microsoft.Extensions.Time.Testing;
using Polly;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Tests.Fixtures;
using RateLimitHeaders.Throttling;

namespace RateLimitHeaders.Polly.Tests.AuditFixes;

/// <summary>
/// Scenarios POLLY01-POLLY08, POLLY27, POLLY28, and POLLY42 from PLAN-audit-fixes.md
/// (tasks T23, T24, and the stop enforcement half of T25): the strategy resolves its state
/// key through the chain (global key, caller-set key string, request on the context, the
/// request that Microsoft.Extensions.Http.Resilience publishes, last observed endpoint),
/// uses a caller-set key verbatim for both lookup and write, falls back to the most recently
/// observed endpoint when nothing resolves (Decision 3), and enforces a server-ordered stop
/// above any algorithm. This closes the Critical finding AUD-02 in
/// TRACKER-adversarial-audit.md (the adversarial-audit findings ledger): under default
/// options the pre-request lookup key never matched the response write key, so proactive
/// throttling never fired.
/// </summary>
public class StateKeyResolutionTests
{
    /// <summary>
    /// The context property name Microsoft.Extensions.Http.Resilience publishes the outgoing
    /// request under; the strategy reads it by name (design item 10 in PLAN-audit-fixes.md).
    /// </summary>
    private static readonly ResiliencePropertyKey<HttpRequestMessage> ResiliencePublishedRequestKey =
        new("Resilience.Http.RequestMessage");

    private sealed class ThrottlingRecorder
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<OnThrottlingArguments> _events = [];

        public Task FirstEvent => _first.Task;

        public List<OnThrottlingArguments> Events
        {
            get { lock (_events) { return _events.ToList(); } }
        }

        public ValueTask Record(OnThrottlingArguments args)
        {
            lock (_events) { _events.Add(args); }
            _first.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AlwaysThrottleAlgorithm : IThrottlingAlgorithm
    {
        public ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo) =>
            rateLimitInfo.IsValid
                ? ThrottlingResult.Throttle(TimeSpan.FromMilliseconds(10), "Always throttle for testing")
                : ThrottlingResult.NoThrottle;
    }

    private sealed class NeverThrottleAlgorithm : IThrottlingAlgorithm
    {
        public ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo) => ThrottlingResult.NoThrottle;
    }

    private static (ResiliencePipeline<HttpResponseMessage> Pipeline, ThrottlingRecorder Recorder, RateLimitStateTracker Tracker, MockHttpHandler Mock, HttpClient Client)
        CreateFixture(Action<RateLimitHeadersStrategyOptions>? configure = null, TimeProvider? timeProvider = null)
    {
        var recorder = new ThrottlingRecorder();
        var tracker = new RateLimitStateTracker(timeProvider ?? TimeProvider.System);
        var mock = new MockHttpHandler();

        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = true;
                options.TrackStatePerEndpoint = true;
                options.ThrottlingAlgorithm = new AlwaysThrottleAlgorithm();
                options.OnThrottling = recorder.Record;
                options.StateStore = tracker;
                if (timeProvider is not null)
                {
                    options.TimeProvider = timeProvider;
                }

                configure?.Invoke(options);
            })
            .Build();

        return (pipeline, recorder, tracker, mock, new HttpClient(mock));
    }

    private static async Task<HttpResponseMessage> ExecuteAsync(
        ResiliencePipeline<HttpResponseMessage> pipeline,
        HttpClient client,
        string url,
        Action<ResilienceContext>? prepareContext = null)
    {
        var context = ResilienceContextPool.Shared.Get();
        try
        {
            prepareContext?.Invoke(context);
            return await pipeline.ExecuteAsync(
                async ctx => await client.GetAsync(url, ctx.CancellationToken),
                context);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    // POLLY01: a caller-set key string drives per-endpoint throttling, and lookup and write
    // use the same string, so the two can never diverge again.
    [Fact]
    public async Task CallerSetKey_DrivesLookupAndWrite()
    {
        var (pipeline, recorder, tracker, mock, client) = CreateFixture();
        mock.QueueRateLimitResponse(50, 30, 100, 60);
        mock.QueueRateLimitResponse(50, 30, 100, 60);

        await ExecuteAsync(pipeline, client, "http://example.com/api/test", ctx => ctx.SetRateLimitStateKey("api.example.com"));
        await ExecuteAsync(pipeline, client, "http://example.com/api/test", ctx => ctx.SetRateLimitStateKey("api.example.com"));

        recorder.Events.Should().ContainSingle();
        recorder.Events[0].StateKey.Should().Be("api.example.com");
        recorder.Events[0].Source.Should().Be(ThrottleDecisionSource.RequestStateKey);
        recorder.Events[0].Delay.Should().Be(TimeSpan.FromMilliseconds(10));

        tracker.TrackedEndpoints.Should().BeEquivalentTo(["api.example.com"],
            "the write must go under the caller's string, never under a request-derived key");
    }

    // POLLY02: the strategy reads the request that Microsoft.Extensions.Http.Resilience
    // publishes, so the standard integration throttles with zero caller code.
    // Red baseline today: zero events (this is the Critical AUD-02 shape).
    [Fact]
    public async Task ResiliencePublishedRequest_DrivesTheLookup()
    {
        var (pipeline, recorder, _, mock, client) = CreateFixture();
        mock.QueueRateLimitResponse(50, 30, 100, 60);
        mock.QueueRateLimitResponse(50, 30, 100, 60);

        for (int i = 0; i < 2; i++)
        {
            await ExecuteAsync(pipeline, client, "https://api.example.com/x", ctx =>
                ctx.Properties.Set(ResiliencePublishedRequestKey, new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/x")));
        }

        recorder.Events.Should().ContainSingle();
        recorder.Events[0].StateKey.Should().Be("https://api.example.com:443");
        recorder.Events[0].Source.Should().Be(ThrottleDecisionSource.ResilienceRequestMessage);
    }

    // POLLY03: the helper SetRateLimitRequest equals setting RequestMessageKey by hand.
    [Fact]
    public async Task SetRateLimitRequest_EqualsSettingThePropertyByHand()
    {
        var (pipeline, recorder, _, mock, client) = CreateFixture();
        mock.QueueRateLimitResponse(50, 30, 100, 60);
        mock.QueueRateLimitResponse(50, 30, 100, 60);

        var firstRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/x");
        var secondRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/x");

        var context1 = ResilienceContextPool.Shared.Get();
        context1.SetRateLimitRequest(firstRequest);
        await pipeline.ExecuteAsync(
            async ctx => await client.SendAsync(firstRequest, ctx.CancellationToken),
            context1);
        ResilienceContextPool.Shared.Return(context1);

        var context2 = ResilienceContextPool.Shared.Get();
        context2.SetRateLimitRequest(secondRequest);
        await pipeline.ExecuteAsync(
            async ctx => await client.SendAsync(secondRequest, ctx.CancellationToken),
            context2);

        recorder.Events.Should().ContainSingle();
        recorder.Events[0].Source.Should().Be(ThrottleDecisionSource.RequestMessage);

        context2.Properties.TryGetValue(RateLimitContextProperties.RequestMessageKey, out var stored).Should().BeTrue();
        stored.Should().BeSameAs(secondRequest);
        ResilienceContextPool.Shared.Return(context2);
    }

    // POLLY27: the chain's precedence holds when two rungs are set at once: the caller-set
    // key string beats the request stored on the context.
    [Fact]
    public async Task CallerSetKey_BeatsTheRequestProperty()
    {
        var (pipeline, recorder, tracker, mock, client) = CreateFixture();
        tracker.UpdateState("caller-key", new RateLimitInfo
        {
            PolicyName = "default",
            Remaining = 50,
            Quota = 100,
            ResetSeconds = 60,
            IsValid = true
        });
        mock.QueueRateLimitResponse(50, 30, 100, 60);

        await ExecuteAsync(pipeline, client, "https://api.example.com/x", ctx =>
        {
            ctx.SetRateLimitStateKey("caller-key");
            ctx.SetRateLimitRequest(new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/x"));
        });

        recorder.Events.Should().ContainSingle();
        recorder.Events[0].StateKey.Should().Be("caller-key");
        recorder.Events[0].Source.Should().Be(ThrottleDecisionSource.RequestStateKey);
    }

    // POLLY04: an explicitly resolved key with no state produces no delay: the fallback to
    // the last observed endpoint must not engage when a key was resolved.
    [Fact]
    public async Task ResolvedKeyWithNoState_ProducesNoDelayAndNoFallback()
    {
        var (pipeline, recorder, _, mock, client) = CreateFixture();
        mock.QueueRateLimitResponse(50, 30, 100, 60);
        mock.QueueRateLimitResponse(50, 30, 100, 60);

        await ExecuteAsync(pipeline, client, "https://api.example.com/x");
        await ExecuteAsync(pipeline, client, "https://api.example.com/x", ctx => ctx.SetRateLimitStateKey("other.example.com"));

        recorder.Events.Should().BeEmpty();
    }

    // POLLY05: the README pipeline shape (no context properties ever set) delays its second
    // request, closing the Critical AUD-02 scenario. This row replaces the deleted test
    // Strategy_WithoutRequestMessageKey_AndPerEndpointTracking_CannotDoProactiveThrottling,
    // which pinned the old zero-event behavior.
    [Fact]
    public async Task ReadmePipelineShape_DelaysTheSecondRequest()
    {
        var (pipeline, recorder, _, mock, client) = CreateFixture();
        mock.QueueRateLimitResponse(50, 30, 100, 60);
        mock.QueueRateLimitResponse(50, 30, 100, 60);

        await ExecuteAsync(pipeline, client, "https://api.example.com/x");
        await ExecuteAsync(pipeline, client, "https://api.example.com/x");

        recorder.Events.Should().ContainSingle();
        recorder.Events[0].StateKey.Should().Be("https://api.example.com:443");
        recorder.Events[0].Source.Should().Be(ThrottleDecisionSource.LastObservedEndpoint);
        recorder.Events[0].Delay.Should().Be(TimeSpan.FromMilliseconds(10));
    }

    // POLLY06: the fallback is opt-out.
    [Fact]
    public async Task Fallback_IsOptOut()
    {
        var (pipeline, recorder, _, mock, client) = CreateFixture(o => o.ThrottleWhenStateKeyUnknown = false);
        mock.QueueRateLimitResponse(50, 30, 100, 60);
        mock.QueueRateLimitResponse(50, 30, 100, 60);

        await ExecuteAsync(pipeline, client, "https://api.example.com/x");
        await ExecuteAsync(pipeline, client, "https://api.example.com/x");

        recorder.Events.Should().BeEmpty();
    }

    // POLLY07: the fallback delay equals the resolved-path delay, not a separate calculation:
    // 5 of 100 remaining with reset 60 and threshold 0.2 computes (0.2 - 0.05) x 60 = 9 s,
    // capped at the 5-second algorithm cap, on both paths.
    [Fact]
    public async Task FallbackDelay_EqualsTheResolvedPathDelay()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        Action<RateLimitHeadersStrategyOptions> usePercentageAlgorithm = o =>
            o.ThrottlingAlgorithm = new PercentageThrottlingAlgorithm(0.2, 1.0, TimeSpan.FromSeconds(5));

        var (pipelineA, recorderA, _, mockA, clientA) = CreateFixture(usePercentageAlgorithm, clock);
        mockA.QueueRateLimitResponse(5, 60, 100, 60);
        mockA.QueueRateLimitResponse(5, 60, 100, 60);

        await ExecuteAsync(pipelineA, clientA, "https://api.example.com/x", ctx =>
            ctx.SetRateLimitRequest(new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/x")));
        var secondA = ExecuteAsync(pipelineA, clientA, "https://api.example.com/x", ctx =>
            ctx.SetRateLimitRequest(new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/x")));
        await recorderA.FirstEvent;
        clock.Advance(TimeSpan.FromSeconds(5));
        await secondA;

        var (pipelineB, recorderB, _, mockB, clientB) = CreateFixture(usePercentageAlgorithm, clock);
        mockB.QueueRateLimitResponse(5, 60, 100, 60);
        mockB.QueueRateLimitResponse(5, 60, 100, 60);

        await ExecuteAsync(pipelineB, clientB, "https://api.example.com/x");
        var secondB = ExecuteAsync(pipelineB, clientB, "https://api.example.com/x");
        await recorderB.FirstEvent;
        clock.Advance(TimeSpan.FromSeconds(5));
        await secondB;

        recorderA.Events.Should().ContainSingle();
        recorderA.Events[0].Delay.Should().Be(TimeSpan.FromSeconds(5));
        recorderA.Events[0].Source.Should().Be(ThrottleDecisionSource.RequestMessage);

        recorderB.Events.Should().ContainSingle();
        recorderB.Events[0].Delay.Should().Be(TimeSpan.FromSeconds(5));
        recorderB.Events[0].Source.Should().Be(ThrottleDecisionSource.LastObservedEndpoint);
    }

    // POLLY08: with per-endpoint tracking off the resolved key is the global key and the
    // fallback never engages.
    [Fact]
    public async Task GlobalTracking_UsesTheGlobalKey()
    {
        var (pipeline, recorder, _, mock, client) = CreateFixture(o => o.TrackStatePerEndpoint = false);
        mock.QueueRateLimitResponse(50, 30, 100, 60);
        mock.QueueRateLimitResponse(50, 30, 100, 60);

        await ExecuteAsync(pipeline, client, "https://api.example.com/x");
        await ExecuteAsync(pipeline, client, "https://api.example.com/x");

        recorder.Events.Should().ContainSingle();
        recorder.Events[0].StateKey.Should().Be("global");
        recorder.Events[0].Source.Should().Be(ThrottleDecisionSource.GlobalTracking);
    }

    // POLLY28: the fallback's documented miss, pinned on purpose: after a healthy response
    // from host B, a third execution headed back to host A is not delayed by A's recorded
    // stop, because the most recently observed endpoint is B. This is the documented cost of
    // Decision 3 (throttle from the last observed endpoint), not a defect to fix silently.
    [Fact]
    public async Task Fallback_MissesWhenHostsAlternate()
    {
        var (pipeline, recorder, _, mock, client) = CreateFixture(o =>
        {
            o.ThrottlingAlgorithm = new PercentageThrottlingAlgorithm();
            // Bound the wait a failing run would impose: execution two legitimately hits
            // host A's stop through the fallback before host B has been observed.
            o.Throttling.MaxExhaustedDelay = TimeSpan.FromMilliseconds(200);
        });

        var tooManyRequests = MockHttpHandler.CreateNoRateLimitResponse(HttpStatusCode.TooManyRequests);
        tooManyRequests.Headers.Add("Retry-After", "300");
        mock.QueueResponse(tooManyRequests);
        mock.QueueRateLimitResponse(90, 30, 100, 60);
        mock.QueueResponse(MockHttpHandler.CreateNoRateLimitResponse());

        await ExecuteAsync(pipeline, client, "http://host-a.example.com/x");
        await ExecuteAsync(pipeline, client, "http://host-b.example.com/x");
        var eventsBeforeThirdExecution = recorder.Events.Count;

        await ExecuteAsync(pipeline, client, "http://host-a.example.com/x");

        recorder.Events.Count.Should().Be(eventsBeforeThirdExecution,
            "the most recently observed endpoint is host B (healthy), so host A's stop is missed");
    }

    // POLLY42: the stop is enforced in the strategy above the algorithm, mirroring the
    // handler: an algorithm that always answers "no throttle" cannot bypass the wait.
    // Red baseline today: the strategy sends immediately.
    [Fact]
    public async Task ServerOrderedStop_IsEnforcedAboveTheAlgorithm()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var (pipeline, recorder, tracker, mock, client) = CreateFixture(o =>
        {
            o.TrackStatePerEndpoint = false;
            o.ThrottlingAlgorithm = new NeverThrottleAlgorithm();
        }, clock);

        tracker.UpdateState("global", RateLimitInfo.CreateFromRetryAfter(120));
        mock.QueueResponse(MockHttpHandler.CreateNoRateLimitResponse());

        var send = ExecuteAsync(pipeline, client, "https://api.example.com/x");
        await recorder.FirstEvent;

        recorder.Events[0].Delay.Should().Be(TimeSpan.FromSeconds(120));
        recorder.Events[0].Reason.Should().Contain("Retry-After");
        mock.RequestCount.Should().Be(0, "the strategy must hold the request until the reset moment");

        clock.Advance(TimeSpan.FromSeconds(120));
        await send;
        mock.RequestCount.Should().Be(1);
    }
}
