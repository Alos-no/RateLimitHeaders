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
    /// <summary>
    /// The context property name under which <c>Microsoft.Extensions.Http.Resilience</c>'s
    /// resilience handler publishes the outgoing request. Read by name so the shipped assembly
    /// needs no reference to that package; the test project references the package and pins
    /// this literal against a real resilience handler.
    /// </summary>
    private static readonly ResiliencePropertyKey<HttpRequestMessage> ResilienceRequestMessageKey =
        new("Resilience.Http.RequestMessage");

    private readonly RateLimitHeadersStrategyOptions _options;
    private readonly RateLimitStateTracker _stateTracker;
    private readonly ResilienceStrategyTelemetry _telemetry;

    /// <summary>
    /// The state key of the most recently observed response, used as the throttling basis
    /// when no key can be resolved before a request (Decision 3 in PLAN-audit-fixes.md).
    /// </summary>
    private volatile string? _lastObservedStateKey;

    public RateLimitHeadersResilienceStrategy(
        RateLimitHeadersStrategyOptions options,
        ResilienceStrategyTelemetry telemetry)
    {
        _options = options;
        _telemetry = telemetry;
        _stateTracker = options.StateStore ?? new RateLimitStateTracker(options.TimeProvider);
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

        var (stateKey, source) = ResolveStateKey(context);
        if (stateKey is null)
        {
            // No key resolved and the last-observed fallback is off or has nothing observed yet
            return;
        }

        if (!_stateTracker.TryGetThrottlingContext(stateKey, out var throttlingContext))
        {
            // Nothing tracked under the resolved key, or the stored window has already
            // elapsed. A resolved key with no state never falls back to another key.
            return;
        }

        var info = throttlingContext.RateLimitInfo;
        TimeSpan delay;
        string? reason;

        if (ExhaustedStateHelper.IsServerOrderedStop(info))
        {
            // A server-ordered stop (a Retry-After header, or zero remaining requests) is
            // enforced here, above the algorithm, so no algorithm can bypass the wait.
            delay = ExhaustedStateHelper.GetStopDelay(info, throttlingContext.TimeUntilReset, _options.Throttling.MaxExhaustedDelay);
            reason = ExhaustedStateHelper.GetStopReason(info, delay);
        }
        else
        {
            var result = _options.ThrottlingAlgorithm.Evaluate(throttlingContext);
            if (!result.ShouldThrottle || result.Delay <= TimeSpan.Zero)
            {
                return;
            }

            delay = result.Delay;
            reason = result.Reason;
        }

        // Raise throttling event
        var throttlingArgs = new OnThrottlingArguments(context, info, delay, reason, stateKey, source);
        await PollyCallbackHelper.SafeInvokeAsync(_options.OnThrottling, throttlingArgs, _telemetry, context, "OnThrottling").ConfigureAwait(context.ContinueOnCapturedContext);

        // Report telemetry
        _telemetry.Report(
            new ResilienceEvent(ResilienceEventSeverity.Information, "OnThrottling"),
            context,
            throttlingArgs);

        // Apply the delay
        await Task.Delay(delay, _options.TimeProvider, context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext);
    }

    /// <summary>
    /// Resolves the state key for the pre-request lookup through the chain (design item 10 in
    /// PLAN-audit-fixes.md): (a) the global key when per-endpoint tracking is off; (b) a
    /// caller-set key string, used verbatim; (c) the request stored under
    /// <see cref="RateLimitContextProperties.RequestMessageKey"/>; (d) the request that
    /// <c>Microsoft.Extensions.Http.Resilience</c> publishes; (e) unresolved, in which case
    /// the most recently observed endpoint is used when
    /// <see cref="RateLimitHeadersStrategyOptions.ThrottleWhenStateKeyUnknown"/> allows it.
    /// </summary>
    private (string? StateKey, ThrottleDecisionSource Source) ResolveStateKey(ResilienceContext context)
    {
        if (!_options.TrackStatePerEndpoint)
        {
            return (RateLimitOptionsHelper.GlobalStateKey, ThrottleDecisionSource.GlobalTracking);
        }

        if (context.Properties.TryGetValue(RateLimitContextProperties.StateKeyKey, out var callerKey)
            && !string.IsNullOrWhiteSpace(callerKey))
        {
            return (callerKey, ThrottleDecisionSource.RequestStateKey);
        }

        if (context.Properties.TryGetValue(RateLimitContextProperties.RequestMessageKey, out var request)
            && request is not null)
        {
            return (_options.GetStateKey(request), ThrottleDecisionSource.RequestMessage);
        }

        if (context.Properties.TryGetValue(ResilienceRequestMessageKey, out var resilienceRequest)
            && resilienceRequest is not null)
        {
            return (_options.GetStateKey(resilienceRequest), ThrottleDecisionSource.ResilienceRequestMessage);
        }

        if (_options.ThrottleWhenStateKeyUnknown && _lastObservedStateKey is string lastObserved)
        {
            return (lastObserved, ThrottleDecisionSource.LastObservedEndpoint);
        }

        return (null, ThrottleDecisionSource.None);
    }

    /// <summary>
    /// Determines the key a response's state is stored under. A caller-set key string is used
    /// verbatim, matching the pre-request lookup, so the two can never diverge; otherwise the
    /// key derives from the response's request URI.
    /// </summary>
    private string GetWriteStateKey(ResilienceContext context, HttpResponseMessage response)
    {
        if (_options.TrackStatePerEndpoint
            && context.Properties.TryGetValue(RateLimitContextProperties.StateKeyKey, out var callerKey)
            && !string.IsNullOrWhiteSpace(callerKey))
        {
            return callerKey;
        }

        return _options.GetStateKey(response.RequestMessage);
    }

    private void UpdateState(string stateKey, RateLimitInfo rateLimitInfo)
    {
        _stateTracker.UpdateState(stateKey, rateLimitInfo);
        _lastObservedStateKey = stateKey;
    }

    private async ValueTask ProcessResponseAsync(ResilienceContext context, HttpResponseMessage response)
    {
        var now = _options.TimeProvider.GetUtcNow();

        // Try to parse rate limit headers
        if (!RateLimitHeaderParser.TryParse(response, out var rateLimitInfo))
        {
            // Even without RateLimit headers, check for Retry-After on the honored statuses
            if (RetryAfterParser.TryGetRetryAfterDelay(response, now, _options.RetryAfterStatusCodes, out var retryAfterDelay))
            {
                rateLimitInfo = RateLimitInfo.CreateFromRetryAfter(retryAfterDelay);
                UpdateState(GetWriteStateKey(context, response), rateLimitInfo);
            }

            return;
        }

        // Per IETF spec: Retry-After takes precedence over RateLimit headers when present.
        // A past-dated Retry-After still applies the override with a zero delay.
        if (RetryAfterParser.TryGetRetryAfterDelay(response, now, _options.RetryAfterStatusCodes, out var overrideRetryAfter))
        {
            rateLimitInfo = rateLimitInfo.WithRetryAfterOverride(overrideRetryAfter);
        }

        // Store in context properties for downstream access
        context.Properties.Set(RateLimitContextProperties.RateLimitInfoKey, rateLimitInfo);

        // Update state tracker under the same key the pre-request lookup would use
        UpdateState(GetWriteStateKey(context, response), rateLimitInfo);

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
        // A quota-only entry (no server-sent remaining count) advertises a limit without
        // reporting consumption, so it must not read as "0 of quota left"
        if (_options.OnQuotaLow is null || rateLimitInfo.Quota <= 0 || !rateLimitInfo.HasRemaining)
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
