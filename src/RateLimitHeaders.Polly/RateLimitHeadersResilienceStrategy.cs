using Polly;
using Polly.Telemetry;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Polly.Internal;

namespace RateLimitHeaders.Polly;

/// <summary>
/// A resilience strategy that parses IETF RateLimit headers from HTTP responses
/// and can proactively throttle requests.
/// </summary>
internal sealed class RateLimitHeadersResilienceStrategy : ResilienceStrategy<HttpResponseMessage>
{
    private readonly RateLimitHeadersStrategyOptions _options;
    private readonly RateLimitStateTracker _stateTracker;
    private readonly ResilienceStrategyTelemetry _telemetry;

    public RateLimitHeadersResilienceStrategy(
        RateLimitHeadersStrategyOptions options,
        ResilienceStrategyTelemetry telemetry)
    {
        _options = options;
        _telemetry = telemetry;
        _stateTracker = new RateLimitStateTracker();
    }

    protected override async ValueTask<Outcome<HttpResponseMessage>> ExecuteCore<TState>(
        Func<ResilienceContext, TState, ValueTask<Outcome<HttpResponseMessage>>> callback,
        ResilienceContext context,
        TState state)
    {
        // Apply proactive throttling before executing the request
        await ApplyProactiveThrottlingAsync(context).ConfigureAwait(context.ContinueOnCapturedContext);

        // Execute the request
        var outcome = await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);

        // Process rate limit headers from successful responses
        if (outcome.Result is not null)
        {
            await ProcessResponseAsync(context, outcome.Result).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        return outcome;
    }

    private async ValueTask ApplyProactiveThrottlingAsync(ResilienceContext context)
    {
        if (!_options.EnableProactiveThrottling)
        {
            return;
        }

        // Try to get the request from context for per-endpoint tracking
        HttpRequestMessage? request = null;
        context.Properties.TryGetValue(RateLimitContextProperties.RequestMessageKey, out request);

        // Get rate limit info for the endpoint (or global if no request available)
        var stateKey = _options.GetStateKey(request);
        var existingInfo = _stateTracker.GetRateLimitInfo(stateKey);
        if (!existingInfo.IsValid)
        {
            return;
        }

        var result = _options.ThrottlingAlgorithm.Evaluate(existingInfo, _stateTracker);
        if (!result.ShouldThrottle || result.Delay <= TimeSpan.Zero)
        {
            return;
        }

        // Raise throttling event
        var throttlingArgs = new OnThrottlingArguments(context, existingInfo, result.Delay, result.Reason);
        await PollyCallbackHelper.SafeInvokeAsync(_options.OnThrottling, throttlingArgs, _telemetry, context, "OnThrottling").ConfigureAwait(context.ContinueOnCapturedContext);

        // Report telemetry
        _telemetry.Report(
            new ResilienceEvent(ResilienceEventSeverity.Information, "OnThrottling"),
            context,
            new OnThrottlingArguments(context, existingInfo, result.Delay, result.Reason));

        // Apply the delay
        await Task.Delay(result.Delay, context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext);
    }

    private async ValueTask ProcessResponseAsync(ResilienceContext context, HttpResponseMessage response)
    {
        // Try to parse rate limit headers
        if (!RateLimitHeaderParser.TryParse(response, out var rateLimitInfo))
        {
            // Even without RateLimit headers, check for Retry-After on 429/503
            if (RetryAfterParser.TryGetRetryAfterSeconds(response, out var retryAfterSeconds))
            {
                rateLimitInfo = RateLimitInfo.CreateFromRetryAfter(retryAfterSeconds);
                var retryAfterStateKey = _options.GetStateKey(response.RequestMessage);
                _stateTracker.UpdateState(retryAfterStateKey, rateLimitInfo);
            }

            return;
        }

        // Per IETF spec: Retry-After takes precedence over RateLimit headers when present
        // This typically occurs on 429/503 responses
        if (RetryAfterParser.TryGetRetryAfterSeconds(response, out var overrideRetryAfter))
        {
            rateLimitInfo = rateLimitInfo.WithRetryAfterOverride(overrideRetryAfter);
        }

        // Store in context properties for downstream access
        context.Properties.Set(RateLimitContextProperties.RateLimitInfoKey, rateLimitInfo);

        // Update state tracker with per-endpoint key (from response.RequestMessage)
        var stateKey = _options.GetStateKey(response.RequestMessage);
        _stateTracker.UpdateState(stateKey, rateLimitInfo);

        // Raise rate limit info event
        var infoArgs = new OnRateLimitInfoArguments(context, rateLimitInfo, response);
        await PollyCallbackHelper.SafeInvokeAsync(_options.OnRateLimitInfo, infoArgs, _telemetry, context, "OnRateLimitInfo").ConfigureAwait(context.ContinueOnCapturedContext);

        // Report telemetry
        _telemetry.Report(
            new ResilienceEvent(ResilienceEventSeverity.Debug, "OnRateLimitInfo"),
            context,
            new OnRateLimitInfoArguments(context, rateLimitInfo, response));

        // Check for quota low condition
        await CheckQuotaLowAsync(context, rateLimitInfo, response).ConfigureAwait(context.ContinueOnCapturedContext);
    }

    private async ValueTask CheckQuotaLowAsync(ResilienceContext context, RateLimitInfo rateLimitInfo, HttpResponseMessage response)
    {
        if (_options.OnQuotaLow is null || rateLimitInfo.Quota <= 0)
        {
            return;
        }

        var quotaPercentage = rateLimitInfo.GetRemainingPercentage();
        if (quotaPercentage > _options.QuotaLowThreshold)
        {
            return;
        }

        var args = new OnQuotaLowArguments(context, rateLimitInfo, response, quotaPercentage, _options.QuotaLowThreshold);

        await PollyCallbackHelper.SafeInvokeAsync(_options.OnQuotaLow, args, _telemetry, context, "OnQuotaLow").ConfigureAwait(context.ContinueOnCapturedContext);

        // Report telemetry
        _telemetry.Report(
            new ResilienceEvent(ResilienceEventSeverity.Warning, "OnQuotaLow"),
            context,
            args);
    }
}
