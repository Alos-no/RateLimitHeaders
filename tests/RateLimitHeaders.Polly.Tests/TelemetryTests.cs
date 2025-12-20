using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Telemetry;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Polly;
using RateLimitHeaders.Tests.Fixtures;
using RateLimitHeaders.Throttling;

namespace RateLimitHeaders.Polly.Tests;

/// <summary>
/// Tests for Polly telemetry reporting in the RateLimitHeaders resilience strategy.
/// Verifies that telemetry events are reported at the correct points with correct severity.
/// </summary>
public class TelemetryTests
{
    [Fact]
    public async Task Strategy_WhenRateLimitHeadersParsed_ShouldReportOnRateLimitInfoTelemetry()
    {
        // Arrange
        var telemetryListener = new TestTelemetryListener();
        var telemetryOptions = new TelemetryOptions
        {
            LoggerFactory = NullLoggerFactory.Instance
        };
        telemetryOptions.TelemetryListeners.Add(telemetryListener);

        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
            })
            .ConfigureTelemetry(telemetryOptions)
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60, "test-policy");
        var client = new HttpClient(mockHandler);

        // Act
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context,
            client);
        ResilienceContextPool.Shared.Return(context);

        // Assert - Should have OnRateLimitInfo telemetry event with Debug severity
        telemetryListener.Events.Should().Contain(e =>
            e.EventName == "OnRateLimitInfo" &&
            e.Severity == ResilienceEventSeverity.Debug);
    }

    [Fact]
    public async Task Strategy_WhenQuotaLow_ShouldReportOnQuotaLowTelemetry()
    {
        // Arrange
        var telemetryListener = new TestTelemetryListener();
        var telemetryOptions = new TelemetryOptions
        {
            LoggerFactory = NullLoggerFactory.Instance
        };
        telemetryOptions.TelemetryListeners.Add(telemetryListener);

        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.QuotaLowThreshold = 0.2; // 20%
                options.OnQuotaLow = _ => ValueTask.CompletedTask; // Enable quota low callback
            })
            .ConfigureTelemetry(telemetryOptions)
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(10, 30, 100, 60, "test-policy"); // 10% remaining
        var client = new HttpClient(mockHandler);

        // Act
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context,
            client);
        ResilienceContextPool.Shared.Return(context);

        // Assert - Should have OnQuotaLow telemetry event with Warning severity
        telemetryListener.Events.Should().Contain(e =>
            e.EventName == "OnQuotaLow" &&
            e.Severity == ResilienceEventSeverity.Warning);
    }

    [Fact]
    public async Task Strategy_WhenThrottling_ShouldReportOnThrottlingTelemetry()
    {
        // Arrange
        var telemetryListener = new TestTelemetryListener();
        var telemetryOptions = new TelemetryOptions
        {
            LoggerFactory = NullLoggerFactory.Instance
        };
        telemetryOptions.TelemetryListeners.Add(telemetryListener);

        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = true;
                options.TrackStatePerEndpoint = false;
                options.ThrottlingAlgorithm = new AlwaysThrottleAlgorithm();
            })
            .ConfigureTelemetry(telemetryOptions)
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60);
        var client = new HttpClient(mockHandler);

        // Act - First request to populate state
        var context1 = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context1,
            client);
        ResilienceContextPool.Shared.Return(context1);

        // Second request triggers throttling
        var context2 = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context2,
            client);
        ResilienceContextPool.Shared.Return(context2);

        // Assert - Should have OnThrottling telemetry event with Information severity
        telemetryListener.Events.Should().Contain(e =>
            e.EventName == "OnThrottling" &&
            e.Severity == ResilienceEventSeverity.Information);
    }

    [Fact]
    public async Task Strategy_TelemetryEventOrder_ShouldBeCorrect()
    {
        // Arrange
        var telemetryListener = new TestTelemetryListener();
        var telemetryOptions = new TelemetryOptions
        {
            LoggerFactory = NullLoggerFactory.Instance
        };
        telemetryOptions.TelemetryListeners.Add(telemetryListener);

        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = true;
                options.TrackStatePerEndpoint = false;
                options.QuotaLowThreshold = 0.2;
                options.ThrottlingAlgorithm = new AlwaysThrottleAlgorithm();
                options.OnQuotaLow = _ => ValueTask.CompletedTask;
            })
            .ConfigureTelemetry(telemetryOptions)
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(50, 30, 100, 60); // First request
        mockHandler.QueueRateLimitResponse(10, 30, 100, 60); // Second request - triggers quota low
        var client = new HttpClient(mockHandler);

        // Act - First request to populate state
        var context1 = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context1,
            client);
        ResilienceContextPool.Shared.Return(context1);

        telemetryListener.Events.Clear();

        // Second request - throttling happens before, then rate limit parsing, then quota low
        var context2 = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context2,
            client);
        ResilienceContextPool.Shared.Return(context2);

        // Assert - Events should be in order: OnThrottling, OnRateLimitInfo, OnQuotaLow
        var eventNames = telemetryListener.Events.Select(e => e.EventName).ToList();
        eventNames.Should().ContainInOrder("OnThrottling", "OnRateLimitInfo", "OnQuotaLow");
    }

    [Fact]
    public async Task Strategy_WithNoRateLimitHeaders_ShouldNotReportTelemetry()
    {
        // Arrange
        var telemetryListener = new TestTelemetryListener();
        var telemetryOptions = new TelemetryOptions
        {
            LoggerFactory = NullLoggerFactory.Instance
        };
        telemetryOptions.TelemetryListeners.Add(telemetryListener);

        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
            })
            .ConfigureTelemetry(telemetryOptions)
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)); // No rate limit headers
        var client = new HttpClient(mockHandler);

        // Act
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context,
            client);
        ResilienceContextPool.Shared.Return(context);

        // Assert - Should not have any RateLimitHeaders-specific events
        telemetryListener.Events.Should().NotContain(e =>
            e.EventName == "OnRateLimitInfo" ||
            e.EventName == "OnQuotaLow" ||
            e.EventName == "OnThrottling");
    }

    #region Helper Classes

    private sealed class TestTelemetryListener : TelemetryListener
    {
        public List<TelemetryEvent> Events { get; } = [];

        public override void Write<TResult, TArgs>(in TelemetryEventArguments<TResult, TArgs> args)
        {
            Events.Add(new TelemetryEvent(args.Event.EventName, args.Event.Severity));
        }
    }

    public readonly record struct TelemetryEvent(string EventName, ResilienceEventSeverity Severity);

    private sealed class AlwaysThrottleAlgorithm : IThrottlingAlgorithm
    {
        public ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo)
        {
            if (!rateLimitInfo.IsValid)
            {
                return ThrottlingResult.NoThrottle;
            }

            return ThrottlingResult.Throttle(TimeSpan.FromMilliseconds(10), "Always throttle for testing");
        }
    }

    #endregion
}
