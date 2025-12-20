using Microsoft.Extensions.Logging;

namespace RateLimitHeaders.Internal;

/// <summary>
/// Helper methods for safely invoking callbacks without breaking the HTTP handler pipeline.
/// </summary>
internal static partial class CallbackHelper
{
    /// <summary>
    /// Safely invokes a synchronous callback, catching and logging any exceptions.
    /// </summary>
    /// <typeparam name="T">The type of the callback argument.</typeparam>
    /// <param name="callback">The callback to invoke.</param>
    /// <param name="args">The argument to pass to the callback.</param>
    /// <param name="logger">The logger for error reporting.</param>
    /// <param name="callbackName">The name of the callback for logging purposes.</param>
    public static void SafeInvoke<T>(
        Action<T>? callback,
        T args,
        ILogger logger,
        string callbackName)
    {
        if (callback is null)
        {
            return;
        }

        try
        {
            callback(args);
        }
        catch (Exception ex)
        {
            LogCallbackError(logger, ex, callbackName);
        }
    }

    /// <summary>
    /// Safely invokes an asynchronous callback, catching and logging any exceptions.
    /// </summary>
    /// <typeparam name="T">The type of the callback argument.</typeparam>
    /// <param name="callback">The async callback to invoke.</param>
    /// <param name="args">The argument to pass to the callback.</param>
    /// <param name="logger">The logger for error reporting.</param>
    /// <param name="callbackName">The name of the callback for logging purposes.</param>
    /// <returns>A task representing the completion of the callback.</returns>
    /// <remarks>
    /// Exceptions are swallowed to prevent breaking the HTTP handler pipeline.
    /// </remarks>
    public static async ValueTask SafeInvokeAsync<T>(
        Func<T, ValueTask>? callback,
        T args,
        ILogger logger,
        string callbackName)
    {
        if (callback is null)
        {
            return;
        }

        try
        {
            await callback(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCallbackError(logger, ex, callbackName);
        }
    }

    /// <summary>
    /// Safely invokes an asynchronous callback without logging (for use where logger is not available).
    /// </summary>
    /// <typeparam name="T">The type of the callback argument.</typeparam>
    /// <param name="callback">The async callback to invoke.</param>
    /// <param name="args">The argument to pass to the callback.</param>
    /// <returns>A task representing the completion of the callback.</returns>
    /// <remarks>
    /// This overload silently swallows exceptions. Prefer using the overload with
    /// ILogger for proper exception reporting.
    /// </remarks>
    public static async ValueTask SafeInvokeAsync<T>(
        Func<T, ValueTask>? callback,
        T args)
    {
        if (callback is null)
        {
            return;
        }

        try
        {
            await callback(args).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Swallow callback exceptions to prevent breaking the pipeline
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in {CallbackName} callback")]
    private static partial void LogCallbackError(ILogger logger, Exception ex, string callbackName);
}
