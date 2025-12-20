using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RateLimitHeaders.Events;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;

namespace RateLimitHeaders.Http;

/// <summary>
/// A delegating handler that parses IETF RateLimit headers from HTTP responses
/// and can proactively throttle requests to avoid hitting rate limits.
/// </summary>
/// <remarks>
/// <para>
/// This handler should be registered as transient and used with IHttpClientFactory.
/// Handler instances are pooled for approximately 2 minutes.
/// </para>
/// <para>
/// The handler parses <c>RateLimit</c> and <c>RateLimit-Policy</c> headers per the
/// IETF specification (draft-ietf-httpapi-ratelimit-headers-10).
/// </para>
/// </remarks>
public sealed partial class RateLimitAwareHandler : DelegatingHandler
{
    private readonly RateLimitAwareOptions _options;
    private readonly RateLimitStateTracker _stateTracker;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitAwareHandler"/> class
    /// with default options.
    /// </summary>
    public RateLimitAwareHandler()
        : this(new RateLimitAwareOptions())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitAwareHandler"/> class
    /// with the specified options.
    /// </summary>
    /// <param name="options">The handler options.</param>
    public RateLimitAwareHandler(RateLimitAwareOptions options)
        : this(options, NullLogger.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitAwareHandler"/> class
    /// with the specified options and logger.
    /// </summary>
    /// <param name="options">The handler options.</param>
    /// <param name="logger">The logger instance.</param>
    public RateLimitAwareHandler(RateLimitAwareOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
        _stateTracker = new RateLimitStateTracker();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitAwareHandler"/> class
    /// with a shared state tracker (for testing purposes).
    /// </summary>
    /// <param name="options">The handler options.</param>
    /// <param name="stateTracker">The shared state tracker.</param>
    /// <param name="logger">The logger instance.</param>
    internal RateLimitAwareHandler(
        RateLimitAwareOptions options,
        RateLimitStateTracker stateTracker,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stateTracker);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _stateTracker = stateTracker;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var stateKey = _options.GetStateKey(request);

        // Apply proactive throttling before sending the request
        await ApplyProactiveThrottlingAsync(request, stateKey, cancellationToken).ConfigureAwait(false);

        // Send the request to the inner handler
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Parse rate limit headers from the response
        await ProcessResponseAsync(request, response, stateKey).ConfigureAwait(false);

        return response;
    }

    private async Task ApplyProactiveThrottlingAsync(
        HttpRequestMessage request,
        string stateKey,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableProactiveThrottling)
        {
            return;
        }

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

        // Invoke throttling callback
        await CallbackHelper.SafeInvokeAsync(
            _options.OnThrottling,
            new ThrottlingEventArgs(existingInfo, request.RequestUri, result.Delay, result.Reason),
            _logger,
            "OnThrottling").ConfigureAwait(false);

        // Log and apply the delay
        LogThrottling(_logger, request.RequestUri, result.Delay.TotalMilliseconds, result.Reason);
        await Task.Delay(result.Delay, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessResponseAsync(HttpRequestMessage request, HttpResponseMessage response, string stateKey)
    {
        // Try to parse rate limit headers
        if (!RateLimitHeaderParser.TryParse(response, out var rateLimitInfo))
        {
            // Even without RateLimit headers, check for Retry-After on 429/503
            if (RetryAfterParser.TryGetRetryAfterSeconds(response, out var retryAfterSeconds))
            {
                rateLimitInfo = RateLimitInfo.CreateFromRetryAfter(retryAfterSeconds);
                _stateTracker.UpdateState(stateKey, rateLimitInfo);
                LogRetryAfter(_logger, request.RequestUri, retryAfterSeconds);
            }

            return;
        }

        // Per IETF spec: Retry-After takes precedence over RateLimit headers when present
        // This typically occurs on 429/503 responses
        if (RetryAfterParser.TryGetRetryAfterSeconds(response, out var overrideRetryAfter))
        {
            LogRetryAfterOverride(_logger, request.RequestUri, overrideRetryAfter, rateLimitInfo.ResetSeconds);
            rateLimitInfo = rateLimitInfo.WithRetryAfterOverride(overrideRetryAfter);
        }

        // Update state tracker
        _stateTracker.UpdateState(stateKey, rateLimitInfo);

        // Store rate limit info in request options for later retrieval via extension method
        request.SetRateLimitInfo(rateLimitInfo);

        // Log the parsed rate limit info
        LogRateLimitParsed(_logger, request.RequestUri, rateLimitInfo.Remaining, rateLimitInfo.Quota, rateLimitInfo.ResetSeconds);

        // Invoke rate limit info callback
        await CallbackHelper.SafeInvokeAsync(
            _options.OnRateLimitInfo,
            new RateLimitEventArgs(rateLimitInfo, request.RequestUri, response),
            _logger,
            "OnRateLimitInfo").ConfigureAwait(false);

        // Check for low quota and invoke callback
        var remainingPercentage = rateLimitInfo.GetRemainingPercentage();
        if (remainingPercentage <= _options.QuotaLowThreshold && rateLimitInfo.Quota > 0)
        {
            LogQuotaLow(_logger, request.RequestUri, remainingPercentage, rateLimitInfo.Remaining, rateLimitInfo.Quota);
            await CallbackHelper.SafeInvokeAsync(
                _options.OnQuotaLow,
                new QuotaLowEventArgs(rateLimitInfo, request.RequestUri, remainingPercentage, _options.QuotaLowThreshold),
                _logger,
                "OnQuotaLow").ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retry-After header received from {Uri}: wait {RetryAfterSeconds}s")]
    private static partial void LogRetryAfter(ILogger logger, Uri? uri, int retryAfterSeconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Retry-After {RetryAfterSeconds}s overrides RateLimit reset time {OriginalResetSeconds}s for {Uri}")]
    private static partial void LogRetryAfterOverride(ILogger logger, Uri? uri, int retryAfterSeconds, int originalResetSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Throttling request to {Uri} for {DelayMs}ms: {Reason}")]
    private static partial void LogThrottling(ILogger logger, Uri? uri, double delayMs, string? reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rate limit parsed for {Uri}: {Remaining}/{Quota} remaining, resets in {ResetSeconds}s")]
    private static partial void LogRateLimitParsed(ILogger logger, Uri? uri, int remaining, int quota, int resetSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Quota low for {Uri}: {RemainingPercentage:P1} remaining ({Remaining}/{Quota})")]
    private static partial void LogQuotaLow(ILogger logger, Uri? uri, double remainingPercentage, int remaining, int quota);
}
