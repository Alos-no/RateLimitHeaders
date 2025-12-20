using Microsoft.Extensions.Configuration;
using Polly;

namespace RateLimitHeaders.Polly;

/// <summary>
/// Extension methods for adding rate limit header parsing to resilience pipelines.
/// </summary>
/// <example>
/// <para>Basic usage with Polly:</para>
/// <code>
/// var pipeline = new ResiliencePipelineBuilder&lt;HttpResponseMessage&gt;()
///     .AddRateLimitHeaders()
///     .Build();
/// </code>
/// <para>With configuration from appsettings.json:</para>
/// <code>
/// var pipeline = new ResiliencePipelineBuilder&lt;HttpResponseMessage&gt;()
///     .AddRateLimitHeaders(configuration.GetSection("RateLimitStrategy"))
///     .Build();
/// </code>
/// </example>
public static class ResiliencePipelineBuilderExtensions
{
    /// <summary>
    /// Adds rate limit header parsing to the resilience pipeline.
    /// </summary>
    /// <param name="builder">The pipeline builder.</param>
    /// <returns>The pipeline builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This strategy parses <c>RateLimit</c> and <c>RateLimit-Policy</c> headers
    /// from HTTP responses and stores the parsed information in the
    /// <see cref="ResilienceContext.Properties"/> using
    /// <see cref="RateLimitContextProperties.RateLimitInfoKey"/>.
    /// </para>
    /// <para>
    /// When proactive throttling is enabled (default), the strategy will delay
    /// requests when the remaining quota falls below the configured threshold.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var pipeline = new ResiliencePipelineBuilder&lt;HttpResponseMessage&gt;()
    ///     .AddRateLimitHeaders()
    ///     .Build();
    ///
    /// var context = ResilienceContextPool.Shared.Get();
    /// try
    /// {
    ///     var response = await pipeline.ExecuteAsync(
    ///         async (ctx) => await httpClient.GetAsync("https://api.example.com", ctx.CancellationToken),
    ///         context);
    ///
    ///     // Access rate limit info from context
    ///     if (context.Properties.TryGetValue(RateLimitContextProperties.RateLimitInfoKey, out var info))
    ///     {
    ///         Console.WriteLine($"Remaining: {info.Remaining}/{info.Quota}");
    ///     }
    /// }
    /// finally
    /// {
    ///     ResilienceContextPool.Shared.Return(context);
    /// }
    /// </code>
    /// </example>
    public static ResiliencePipelineBuilder<HttpResponseMessage> AddRateLimitHeaders(
        this ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        return builder.AddRateLimitHeaders(new RateLimitHeadersStrategyOptions());
    }

    /// <summary>
    /// Adds rate limit header parsing to the resilience pipeline with the specified options.
    /// </summary>
    /// <param name="builder">The pipeline builder.</param>
    /// <param name="options">The strategy options.</param>
    /// <returns>The pipeline builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This strategy parses <c>RateLimit</c> and <c>RateLimit-Policy</c> headers
    /// from HTTP responses and stores the parsed information in the
    /// <see cref="ResilienceContext.Properties"/> using
    /// <see cref="RateLimitContextProperties.RateLimitInfoKey"/>.
    /// </para>
    /// <para>
    /// When proactive throttling is enabled (default), the strategy will delay
    /// requests when the remaining quota falls below the configured threshold.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var options = new RateLimitHeadersStrategyOptions
    /// {
    ///     EnableProactiveThrottling = true,
    ///     QuotaLowThreshold = 0.2,
    ///     OnQuotaLow = args =>
    ///     {
    ///         Console.WriteLine($"Low quota warning: {args.QuotaPercentage:P0}");
    ///         return ValueTask.CompletedTask;
    ///     }
    /// };
    ///
    /// var pipeline = new ResiliencePipelineBuilder&lt;HttpResponseMessage&gt;()
    ///     .AddRateLimitHeaders(options)
    ///     .Build();
    /// </code>
    /// </example>
    public static ResiliencePipelineBuilder<HttpResponseMessage> AddRateLimitHeaders(
        this ResiliencePipelineBuilder<HttpResponseMessage> builder,
        RateLimitHeadersStrategyOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        return builder.AddStrategy(
            context => new RateLimitHeadersResilienceStrategy(options, context.Telemetry),
            options);
    }

    /// <summary>
    /// Adds rate limit header parsing to the resilience pipeline with a configuration action.
    /// </summary>
    /// <param name="builder">The pipeline builder.</param>
    /// <param name="configure">An action to configure the strategy options.</param>
    /// <returns>The pipeline builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This strategy parses <c>RateLimit</c> and <c>RateLimit-Policy</c> headers
    /// from HTTP responses and stores the parsed information in the
    /// <see cref="ResilienceContext.Properties"/> using
    /// <see cref="RateLimitContextProperties.RateLimitInfoKey"/>.
    /// </para>
    /// <para>
    /// When proactive throttling is enabled (default), the strategy will delay
    /// requests when the remaining quota falls below the configured threshold.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var pipeline = new ResiliencePipelineBuilder&lt;HttpResponseMessage&gt;()
    ///     .AddRateLimitHeaders(options =>
    ///     {
    ///         options.EnableProactiveThrottling = true;
    ///         options.QuotaLowThreshold = 0.15;
    ///         options.OnRateLimitInfo = args =>
    ///         {
    ///             logger.LogDebug("Rate limit: {Remaining}/{Quota}", args.RateLimitInfo.Remaining, args.RateLimitInfo.Quota);
    ///             return ValueTask.CompletedTask;
    ///         };
    ///     })
    ///     .Build();
    /// </code>
    /// </example>
    public static ResiliencePipelineBuilder<HttpResponseMessage> AddRateLimitHeaders(
        this ResiliencePipelineBuilder<HttpResponseMessage> builder,
        Action<RateLimitHeadersStrategyOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new RateLimitHeadersStrategyOptions();
        configure(options);

        return builder.AddRateLimitHeaders(options);
    }

    /// <summary>
    /// Adds rate limit header parsing to the resilience pipeline with options bound from
    /// an <see cref="IConfiguration"/> section.
    /// </summary>
    /// <param name="builder">The pipeline builder.</param>
    /// <param name="configuration">The configuration section to bind options from.</param>
    /// <returns>The pipeline builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Bindable properties from configuration:
    /// <list type="bullet">
    /// <item><c>EnableProactiveThrottling</c> - boolean (default: true)</item>
    /// <item><c>QuotaLowThreshold</c> - double between 0.0 and 1.0 (default: 0.1)</item>
    /// <item><c>TrackStatePerEndpoint</c> - boolean (default: true)</item>
    /// </list>
    /// Callbacks (OnRateLimitInfo, OnQuotaLow, OnThrottling) cannot be bound from configuration
    /// and must be set programmatically using the overload with configure action.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // appsettings.json:
    /// // {
    /// //   "RateLimitStrategy": {
    /// //     "EnableProactiveThrottling": true,
    /// //     "QuotaLowThreshold": 0.2,
    /// //     "TrackStatePerEndpoint": true
    /// //   }
    /// // }
    ///
    /// var pipeline = new ResiliencePipelineBuilder&lt;HttpResponseMessage&gt;()
    ///     .AddRateLimitHeaders(configuration.GetSection("RateLimitStrategy"))
    ///     .Build();
    /// </code>
    /// </example>
    public static ResiliencePipelineBuilder<HttpResponseMessage> AddRateLimitHeaders(
        this ResiliencePipelineBuilder<HttpResponseMessage> builder,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new RateLimitHeadersStrategyOptions();
        configuration.Bind(options);

        return builder.AddRateLimitHeaders(options);
    }

    /// <summary>
    /// Adds rate limit header parsing to the resilience pipeline with options bound from
    /// an <see cref="IConfiguration"/> section, with an additional configure action for callbacks.
    /// </summary>
    /// <param name="builder">The pipeline builder.</param>
    /// <param name="configuration">The configuration section to bind options from.</param>
    /// <param name="configure">An action to configure additional options (typically callbacks).</param>
    /// <returns>The pipeline builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This overload allows combining configuration file settings with programmatic callback setup.
    /// The configuration is applied first, then the configure action is invoked.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var pipeline = new ResiliencePipelineBuilder&lt;HttpResponseMessage&gt;()
    ///     .AddRateLimitHeaders(
    ///         configuration.GetSection("RateLimitStrategy"),
    ///         options => options.OnQuotaLow = args =>
    ///         {
    ///             logger.LogWarning("Quota low: {Percentage:P0}", args.QuotaPercentage);
    ///             return ValueTask.CompletedTask;
    ///         })
    ///     .Build();
    /// </code>
    /// </example>
    public static ResiliencePipelineBuilder<HttpResponseMessage> AddRateLimitHeaders(
        this ResiliencePipelineBuilder<HttpResponseMessage> builder,
        IConfiguration configuration,
        Action<RateLimitHeadersStrategyOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new RateLimitHeadersStrategyOptions();
        configuration.Bind(options);
        configure(options);

        return builder.AddRateLimitHeaders(options);
    }
}
