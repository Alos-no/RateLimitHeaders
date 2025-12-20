using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RateLimitHeaders.Http;

namespace RateLimitHeaders;

/// <summary>
/// Extension methods for adding rate limit aware handlers to an IHttpClientBuilder.
/// </summary>
/// <example>
/// <para>Basic usage with default options:</para>
/// <code>
/// services.AddHttpClient("MyApi")
///     .AddRateLimitAwareHandler();
/// </code>
/// <para>Configuration from appsettings.json:</para>
/// <code>
/// // appsettings.json:
/// // {
/// //   "RateLimitHandler": {
/// //     "EnableProactiveThrottling": true,
/// //     "QuotaLowThreshold": 0.2,
/// //     "TrackStatePerEndpoint": true
/// //   }
/// // }
///
/// services.AddHttpClient("MyApi")
///     .AddRateLimitAwareHandler(configuration.GetSection("RateLimitHandler"));
/// </code>
/// <para>Using IOptions&lt;T&gt; pattern:</para>
/// <code>
/// services.Configure&lt;RateLimitAwareOptions&gt;("MyApi", configuration.GetSection("RateLimitHandler"));
/// services.AddHttpClient("MyApi")
///     .AddRateLimitAwareHandler("MyApi");
/// </code>
/// </example>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds a <see cref="RateLimitAwareHandler"/> to the HTTP client builder with default options.
    /// </summary>
    /// <param name="builder">The HTTP client builder.</param>
    /// <returns>The HTTP client builder for chaining.</returns>
    /// <remarks>
    /// The handler is added as a transient service as required by IHttpClientFactory.
    /// Handler instances are pooled for approximately 2 minutes.
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddHttpClient("MyApi", client => client.BaseAddress = new Uri("https://api.example.com"))
    ///     .AddRateLimitAwareHandler();
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddRateLimitAwareHandler(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddHttpMessageHandler(sp =>
        {
            var options = new RateLimitAwareOptions();

            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger<RateLimitAwareHandler>()
                         ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RateLimitAwareHandler>.Instance;

            return new RateLimitAwareHandler(options, logger);
        });
    }

    /// <summary>
    /// Adds a <see cref="RateLimitAwareHandler"/> to the HTTP client builder with the specified options.
    /// </summary>
    /// <param name="builder">The HTTP client builder.</param>
    /// <param name="configure">An optional action to configure the handler options.</param>
    /// <returns>The HTTP client builder for chaining.</returns>
    /// <remarks>
    /// The handler is added as a transient service as required by IHttpClientFactory.
    /// Handler instances are pooled for approximately 2 minutes.
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddHttpClient("MyApi")
    ///     .AddRateLimitAwareHandler(options =>
    ///     {
    ///         options.EnableProactiveThrottling = true;
    ///         options.QuotaLowThreshold = 0.2;
    ///         options.OnQuotaLow = args => Console.WriteLine($"Quota low: {args.RateLimitInfo.Remaining}");
    ///     });
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddRateLimitAwareHandler(
        this IHttpClientBuilder builder,
        Action<RateLimitAwareOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddHttpMessageHandler(sp =>
        {
            var options = new RateLimitAwareOptions();
            configure?.Invoke(options);

            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger<RateLimitAwareHandler>()
                         ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RateLimitAwareHandler>.Instance;

            return new RateLimitAwareHandler(options, logger);
        });
    }

    /// <summary>
    /// Adds a <see cref="RateLimitAwareHandler"/> to the HTTP client builder with options configured from a callback
    /// that has access to the service provider.
    /// </summary>
    /// <param name="builder">The HTTP client builder.</param>
    /// <param name="configure">An action to configure the handler options with access to the service provider.</param>
    /// <returns>The HTTP client builder for chaining.</returns>
    /// <remarks>
    /// The handler is added as a transient service as required by IHttpClientFactory.
    /// Handler instances are pooled for approximately 2 minutes.
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddHttpClient("MyApi")
    ///     .AddRateLimitAwareHandler((sp, options) =>
    ///     {
    ///         var telemetry = sp.GetRequiredService&lt;ITelemetryService&gt;();
    ///         options.OnRateLimitInfo = args => telemetry.TrackRateLimit(args.RateLimitInfo);
    ///     });
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddRateLimitAwareHandler(
        this IHttpClientBuilder builder,
        Action<IServiceProvider, RateLimitAwareOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        return builder.AddHttpMessageHandler(sp =>
        {
            var options = new RateLimitAwareOptions();
            configure(sp, options);

            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger<RateLimitAwareHandler>()
                         ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RateLimitAwareHandler>.Instance;

            return new RateLimitAwareHandler(options, logger);
        });
    }

    /// <summary>
    /// Adds a <see cref="RateLimitAwareHandler"/> to the HTTP client builder with options bound from
    /// an <see cref="IConfiguration"/> section.
    /// </summary>
    /// <param name="builder">The HTTP client builder.</param>
    /// <param name="configuration">The configuration section to bind options from.</param>
    /// <returns>The HTTP client builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The handler is added as a transient service as required by IHttpClientFactory.
    /// Handler instances are pooled for approximately 2 minutes.
    /// </para>
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
    /// //   "RateLimitHandler": {
    /// //     "EnableProactiveThrottling": true,
    /// //     "QuotaLowThreshold": 0.2,
    /// //     "TrackStatePerEndpoint": true
    /// //   }
    /// // }
    ///
    /// services.AddHttpClient("MyApi")
    ///     .AddRateLimitAwareHandler(configuration.GetSection("RateLimitHandler"));
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddRateLimitAwareHandler(
        this IHttpClientBuilder builder,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        return builder.AddHttpMessageHandler(sp =>
        {
            var options = new RateLimitAwareOptions();
            configuration.Bind(options);

            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger<RateLimitAwareHandler>()
                         ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RateLimitAwareHandler>.Instance;

            return new RateLimitAwareHandler(options, logger);
        });
    }

    /// <summary>
    /// Adds a <see cref="RateLimitAwareHandler"/> to the HTTP client builder with options bound from
    /// an <see cref="IConfiguration"/> section, with an additional configure action for callbacks.
    /// </summary>
    /// <param name="builder">The HTTP client builder.</param>
    /// <param name="configuration">The configuration section to bind options from.</param>
    /// <param name="configure">An action to configure additional options (typically callbacks).</param>
    /// <returns>The HTTP client builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This overload allows combining configuration file settings with programmatic callback setup.
    /// The configuration is applied first, then the configure action is invoked.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddHttpClient("MyApi")
    ///     .AddRateLimitAwareHandler(
    ///         configuration.GetSection("RateLimitHandler"),
    ///         options => options.OnQuotaLow = args => logger.LogWarning("Quota low!"));
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddRateLimitAwareHandler(
        this IHttpClientBuilder builder,
        IConfiguration configuration,
        Action<RateLimitAwareOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configure);

        return builder.AddHttpMessageHandler(sp =>
        {
            var options = new RateLimitAwareOptions();
            configuration.Bind(options);
            configure(options);

            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger<RateLimitAwareHandler>()
                         ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RateLimitAwareHandler>.Instance;

            return new RateLimitAwareHandler(options, logger);
        });
    }

    /// <summary>
    /// Adds a <see cref="RateLimitAwareHandler"/> to the HTTP client builder using named options
    /// from the <see cref="IOptionsMonitor{TOptions}"/> pattern.
    /// </summary>
    /// <param name="builder">The HTTP client builder.</param>
    /// <param name="optionsName">The name of the options to use. Use <see cref="Options.DefaultName"/> for unnamed options.</param>
    /// <returns>The HTTP client builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This overload integrates with the IOptions&lt;T&gt; pattern, allowing options to be registered
    /// separately and resolved at runtime. This is useful for scenarios where options need to be
    /// shared across multiple components or when using options validation.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Register options with a name matching the client name
    /// services.Configure&lt;RateLimitAwareOptions&gt;("MyApi", options =>
    /// {
    ///     options.EnableProactiveThrottling = true;
    ///     options.QuotaLowThreshold = 0.2;
    /// });
    ///
    /// // Or bind from configuration
    /// services.Configure&lt;RateLimitAwareOptions&gt;("MyApi", configuration.GetSection("RateLimitHandler"));
    ///
    /// // Use the named options
    /// services.AddHttpClient("MyApi")
    ///     .AddRateLimitAwareHandler("MyApi");
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddRateLimitAwareHandler(
        this IHttpClientBuilder builder,
        string optionsName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(optionsName);

        return builder.AddHttpMessageHandler(sp =>
        {
            var optionsMonitor = sp.GetRequiredService<IOptionsMonitor<RateLimitAwareOptions>>();
            var options = optionsMonitor.Get(optionsName);

            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger<RateLimitAwareHandler>()
                         ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RateLimitAwareHandler>.Instance;

            return new RateLimitAwareHandler(options, logger);
        });
    }
}
