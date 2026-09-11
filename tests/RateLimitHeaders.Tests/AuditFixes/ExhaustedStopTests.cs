using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RateLimitHeaders.Events;
using RateLimitHeaders.Http;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Tests.Fixtures;
using RateLimitHeaders.Throttling;

namespace RateLimitHeaders.Tests.AuditFixes;

/// <summary>
/// Scenarios STATE12, STATE13, STATE16-STATE21, STATE42, STATE43 from PLAN-audit-fixes.md
/// (tasks T9 and T10): the handler itself waits until the reset moment when the stored state
/// carries a Retry-After or reads exhausted, before any throttling algorithm is consulted;
/// within a window the proactive delay shrinks with elapsed time. All timing runs on a
/// <see cref="FakeTimeProvider"/>; each scenario waits for the first OnThrottling callback
/// (the barrier) before advancing the clock.
/// </summary>
public class ExhaustedStopTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class ThrottlingRecorder
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<ThrottlingEventArgs> _events = [];

        public Task FirstEvent => _first.Task;

        public List<ThrottlingEventArgs> Events
        {
            get { lock (_events) { return _events.ToList(); } }
        }

        public ValueTask Record(ThrottlingEventArgs args)
        {
            lock (_events) { _events.Add(args); }
            _first.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NeverThrottleAlgorithm : IThrottlingAlgorithm
    {
        // The shape the library's own extension docs teach: bail out on Quota <= 0.
        public ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo)
        {
            if (!rateLimitInfo.IsValid || rateLimitInfo.Quota <= 0)
            {
                return ThrottlingResult.NoThrottle;
            }

            return ThrottlingResult.NoThrottle;
        }
    }

    private static (FakeTimeProvider Clock, RateLimitStateTracker Tracker, ThrottlingRecorder Recorder, MockHttpHandler Mock, HttpClient Client)
        CreateFixture(IThrottlingAlgorithm? algorithm = null)
    {
        var clock = new FakeTimeProvider(Start);
        var tracker = new RateLimitStateTracker(clock);
        var recorder = new ThrottlingRecorder();

        var options = new RateLimitAwareOptions
        {
            EnableProactiveThrottling = true,
            TrackStatePerEndpoint = false,
            TimeProvider = clock,
            OnThrottling = recorder.Record
        };
        if (algorithm is not null)
        {
            options.ThrottlingAlgorithm = algorithm;
        }

        var mock = new MockHttpHandler();
        var handler = new RateLimitAwareHandler(options, tracker, NullLogger.Instance)
        {
            InnerHandler = mock
        };

        return (clock, tracker, recorder, mock, new HttpClient(handler));
    }

    private static HttpResponseMessage RetryAfterOnlyResponse(int retryAfterSeconds, HttpStatusCode status = HttpStatusCode.TooManyRequests)
    {
        var response = MockHttpHandler.CreateNoRateLimitResponse(status);
        response.Headers.Add("Retry-After", retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }

    // STATE16: Retry-After state throttles despite Quota 0.
    // Red baseline today: the algorithm's Quota <= 0 guard returns no throttle for exactly this state.
    [Fact]
    public async Task RetryAfterState_ProducesTheFullWait()
    {
        var (clock, tracker, recorder, mock, client) = CreateFixture();
        tracker.UpdateState("global", RateLimitInfo.CreateFromRetryAfter(120));
        mock.QueueResponse(MockHttpHandler.CreateNoRateLimitResponse());

        var send = client.GetAsync("http://example.com/api/test");
        await recorder.FirstEvent;

        recorder.Events[0].Delay.Should().Be(TimeSpan.FromSeconds(120));
        recorder.Events[0].Reason.Should().Contain("Retry-After");
        send.IsCompleted.Should().BeFalse("the handler must hold the request until the reset moment");
        mock.RequestCount.Should().Be(0);

        clock.Advance(TimeSpan.FromSeconds(120));
        await send;
        mock.RequestCount.Should().Be(1);
    }

    // STATE42: the stop check runs in the handler, above the algorithm, so a custom
    // algorithm that always answers "no throttle" cannot lose the wait.
    [Fact]
    public async Task CustomAlgorithm_CannotBypassTheWait()
    {
        var (clock, tracker, recorder, mock, client) = CreateFixture(new NeverThrottleAlgorithm());
        tracker.UpdateState("global", RateLimitInfo.CreateFromRetryAfter(120));

        var send = client.GetAsync("http://example.com/api/test");
        await recorder.FirstEvent;

        recorder.Events[0].Delay.Should().Be(TimeSpan.FromSeconds(120));
        mock.RequestCount.Should().Be(0);

        clock.Advance(TimeSpan.FromSeconds(120));
        await send;
        mock.RequestCount.Should().Be(1);
    }

    // STATE43: an exhausted state with no reset still stops the client:
    // 1 second when nothing else is known, the window length when the policy supplied one.
    [Fact]
    public async Task ExhaustedWithoutReset_WaitsTheFloor()
    {
        var (clock, tracker, recorder, _, client) = CreateFixture();
        tracker.UpdateState("global", new RateLimitInfo { PolicyName = "default", Remaining = 0, IsValid = true });

        var send = client.GetAsync("http://example.com/api/test");
        await recorder.FirstEvent;
        recorder.Events[0].Delay.Should().Be(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        await send;
    }

    [Fact]
    public async Task ExhaustedWithoutResetButWithWindow_WaitsTheWindow()
    {
        var (clock, tracker, recorder, _, client) = CreateFixture();
        tracker.UpdateState("global", new RateLimitInfo
        {
            PolicyName = "default",
            Remaining = 0,
            WindowSeconds = 60,
            IsValid = true
        });

        var send = client.GetAsync("http://example.com/api/test");
        await recorder.FirstEvent;
        recorder.Events[0].Delay.Should().Be(TimeSpan.FromSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(60));
        await send;
    }

    // STATE17: the audit's Critical scenario end to end: after a 429 with Retry-After: 120,
    // the next request waits. Red baseline today: request two goes through immediately.
    [Fact]
    public async Task After429WithRetryAfter_TheNextRequestWaits()
    {
        var (clock, _, recorder, mock, client) = CreateFixture();
        mock.QueueResponse(RetryAfterOnlyResponse(120));
        mock.QueueResponse(MockHttpHandler.CreateNoRateLimitResponse());

        (await client.GetAsync("http://example.com/api/test")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        mock.RequestCount.Should().Be(1);

        var second = client.GetAsync("http://example.com/api/test");
        await recorder.FirstEvent;

        clock.Advance(TimeSpan.FromSeconds(119));
        mock.RequestCount.Should().Be(1, "at 119 seconds the stop is still in force");
        second.IsCompleted.Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(1));
        await second;
        mock.RequestCount.Should().Be(2, "at 120 seconds the reset moment has arrived");
    }

    // STATE18: after the reset moment passes, traffic resumes undelayed.
    [Fact]
    public async Task AfterTheResetMoment_TrafficResumesUndelayed()
    {
        var (clock, tracker, recorder, mock, client) = CreateFixture();
        mock.QueueResponse(RetryAfterOnlyResponse(120));
        mock.QueueResponse(MockHttpHandler.CreateNoRateLimitResponse());

        await client.GetAsync("http://example.com/api/test");

        clock.Advance(TimeSpan.FromSeconds(121));
        tracker.GetRateLimitInfo("global").IsValid.Should().BeFalse();

        await client.GetAsync("http://example.com/api/test");
        recorder.Events.Should().BeEmpty();
        mock.RequestCount.Should().Be(2);
    }

    // STATE19: a hostile Retry-After is bounded by Throttling.MaxExhaustedDelay (default 5 minutes).
    [Fact]
    public async Task HostileRetryAfter_IsBoundedByTheExhaustedDelayCap()
    {
        var (clock, tracker, recorder, _, client) = CreateFixture();
        tracker.UpdateState("global", RateLimitInfo.CreateFromRetryAfter(86400));

        var send = client.GetAsync("http://example.com/api/test");
        await recorder.FirstEvent;
        recorder.Events[0].Delay.Should().Be(TimeSpan.FromMinutes(5));
        clock.Advance(TimeSpan.FromMinutes(5));
        await send;
    }

    // STATE20: an exhausted window with a known quota waits the window, not the 5-second
    // algorithm cap (Decision 4). Red baseline today: 5 seconds.
    [Fact]
    public async Task ExhaustedWindowWithKnownQuota_WaitsTheFullWindow()
    {
        var (clock, tracker, recorder, _, client) = CreateFixture();
        tracker.UpdateState("global", new RateLimitInfo
        {
            PolicyName = "default",
            Remaining = 0,
            Quota = 100,
            ResetSeconds = 60,
            IsValid = true
        });

        var send = client.GetAsync("http://example.com/api/test");
        await recorder.FirstEvent;
        recorder.Events[0].Delay.Should().Be(TimeSpan.FromSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(60));
        await send;
    }

    // STATE21: Retry-After beats the RateLimit reset in the applied delay.
    // Red baseline today: (0.1 - 0.4) is above threshold... the stored info still shows
    // Remaining 40 and the next request is at most delayed by the 5-second algorithm cap.
    [Fact]
    public async Task RetryAfterOverride_DrivesTheAppliedDelay()
    {
        var (clock, tracker, recorder, mock, client) = CreateFixture();
        mock.QueueTooManyRequestsResponse(retryAfterSeconds: 120, remaining: 40, resetSeconds: 30, quota: 100, windowSeconds: 60);
        mock.QueueResponse(MockHttpHandler.CreateNoRateLimitResponse());

        await client.GetAsync("http://example.com/api/test");

        var stored = tracker.GetRateLimitInfo("global");
        stored.HasRetryAfter.Should().BeTrue();
        stored.Remaining.Should().Be(0);
        stored.ResetSeconds.Should().Be(120);

        var second = client.GetAsync("http://example.com/api/test");
        await recorder.FirstEvent;
        recorder.Events[0].Delay.Should().Be(TimeSpan.FromSeconds(120));
        clock.Advance(TimeSpan.FromSeconds(120));
        await second;
    }

    // STATE12: the audit's idle-client scenario produces no delay: ten minutes after a
    // 60-second window was observed, the state has expired and nothing throttles.
    // Red baseline today: the handler sleeps 3 seconds on the stale snapshot.
    [Fact]
    public async Task StaleState_ProducesNoDelay()
    {
        var (clock, tracker, recorder, mock, client) = CreateFixture();
        tracker.UpdateState("global", new RateLimitInfo
        {
            PolicyName = "default",
            Remaining = 5,
            Quota = 100,
            ResetSeconds = 60,
            IsValid = true
        });

        clock.Advance(TimeSpan.FromMinutes(10));
        await client.GetAsync("http://example.com/api/test");

        recorder.Events.Should().BeEmpty();
        mock.RequestCount.Should().Be(1);
    }

    // STATE13: within the window the proactive delay shrinks proportionally:
    // 45 seconds into a 60-second window, (0.10 - 0.05) x 15 s = 750 ms, not 3000 ms.
    [Fact]
    public async Task WithinTheWindow_TheDelayShrinksWithElapsedTime()
    {
        var (clock, tracker, recorder, _, client) = CreateFixture();
        tracker.UpdateState("global", new RateLimitInfo
        {
            PolicyName = "default",
            Remaining = 5,
            Quota = 100,
            ResetSeconds = 60,
            IsValid = true
        });

        clock.Advance(TimeSpan.FromSeconds(45));
        var send = client.GetAsync("http://example.com/api/test");
        await recorder.FirstEvent;

        recorder.Events[0].Delay.Should().Be(TimeSpan.FromMilliseconds(750));
        clock.Advance(TimeSpan.FromMilliseconds(750));
        await send;
    }
}
