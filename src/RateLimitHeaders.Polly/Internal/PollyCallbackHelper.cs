using Polly;
using Polly.Telemetry;

namespace RateLimitHeaders.Polly.Internal;

/// <summary>
/// Helper methods for safely invoking callbacks without breaking the Polly resilience pipeline.
/// </summary>
internal static class PollyCallbackHelper
{
    /// <summary>
    /// Safely invokes an asynchronous callback, catching and reporting any exceptions via Polly telemetry.
    /// </summary>
    /// <typeparam name="T">The type of the callback argument.</typeparam>
    /// <param name="callback">The async callback to invoke.</param>
    /// <param name="args">The argument to pass to the callback.</param>
    /// <param name="telemetry">The Polly telemetry for error reporting.</param>
    /// <param name="context">The resilience context.</param>
    /// <param name="callbackName">The name of the callback for telemetry purposes.</param>
    /// <returns>A task representing the completion of the callback.</returns>
    /// <remarks>
    /// Exceptions are swallowed to prevent breaking the resilience pipeline.
    /// </remarks>
    public static async ValueTask SafeInvokeAsync<T>(
        Func<T, ValueTask>? callback,
        T args,
        ResilienceStrategyTelemetry telemetry,
        ResilienceContext context,
        string callbackName)
    {
        if (callback is null)
        {
            return;
        }

        try
        {
            await callback(args).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        catch (Exception ex)
        {
            // Report callback exception via Polly telemetry
            telemetry.Report(
                new ResilienceEvent(ResilienceEventSeverity.Error, $"{callbackName}CallbackError"),
                context,
                Outcome.FromException<HttpResponseMessage>(ex));
        }
    }
}
