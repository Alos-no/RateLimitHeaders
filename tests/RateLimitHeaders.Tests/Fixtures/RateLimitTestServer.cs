using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace RateLimitHeaders.Tests.Fixtures;

/// <summary>
/// A configurable test server that simulates an API with rate limit headers.
/// </summary>
public sealed class RateLimitTestServer : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly TestServer _testServer;
    private readonly RateLimitTestServerOptions _options;

    private int _requestCount;
    private int _remainingQuota;

    /// <summary>
    /// Gets the number of requests received by the server.
    /// </summary>
    public int RequestCount => _requestCount;

    /// <summary>
    /// Gets the current remaining quota.
    /// </summary>
    public int RemainingQuota => _remainingQuota;

    /// <summary>
    /// Gets an HttpClient configured to use this test server.
    /// </summary>
    public HttpClient Client => _testServer.CreateClient();

    private RateLimitTestServer(IHost host, TestServer testServer, RateLimitTestServerOptions options)
    {
        _host = host;
        _testServer = testServer;
        _options = options;
        _remainingQuota = options.InitialQuota;
    }

    /// <summary>
    /// Creates a new test server with the specified options.
    /// </summary>
    public static async Task<RateLimitTestServer> CreateAsync(RateLimitTestServerOptions? options = null)
    {
        options ??= new RateLimitTestServerOptions();

        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .Configure(app =>
                    {
                        app.Run(async context =>
                        {
                            await HandleRequestAsync(context, options);
                        });
                    });
            });

        var host = await hostBuilder.StartAsync();
        var testServer = host.GetTestServer();

        return new RateLimitTestServer(host, testServer, options);
    }

    private static async Task HandleRequestAsync(HttpContext context, RateLimitTestServerOptions options)
    {
        // Simulate processing delay if configured
        if (options.ResponseDelay > TimeSpan.Zero)
        {
            await Task.Delay(options.ResponseDelay, context.RequestAborted);
        }

        // Track request count (thread-safe)
        var requestNumber = Interlocked.Increment(ref options.RequestCounter);

        // Calculate remaining quota
        var remaining = Math.Max(0, options.InitialQuota - requestNumber);
        var resetSeconds = options.WindowSeconds;

        // Check if we should return 429
        if (remaining <= 0 && options.Return429WhenExhausted)
        {
            context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
            context.Response.Headers["Retry-After"] = options.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            context.Response.StatusCode = (int)HttpStatusCode.OK;
        }

        // Add rate limit headers
        context.Response.Headers["RateLimit"] =
            $"\"{options.PolicyName}\";r={remaining};t={resetSeconds}";
        context.Response.Headers["RateLimit-Policy"] =
            $"\"{options.PolicyName}\";q={options.InitialQuota};w={options.WindowSeconds}";

        // Custom header callback if configured
        options.OnRequest?.Invoke(context, requestNumber, remaining);

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($"{{\"request\":{requestNumber},\"remaining\":{remaining}}}");
    }

    /// <summary>
    /// Resets the server state.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _requestCount, 0);
        Interlocked.Exchange(ref _remainingQuota, _options.InitialQuota);
        Interlocked.Exchange(ref _options.RequestCounter, 0);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

/// <summary>
/// Configuration options for <see cref="RateLimitTestServer"/>.
/// </summary>
public sealed class RateLimitTestServerOptions
{
    /// <summary>
    /// Gets or sets the initial quota (maximum requests per window).
    /// Default is 100.
    /// </summary>
    public int InitialQuota { get; set; } = 100;

    /// <summary>
    /// Gets or sets the window duration in seconds.
    /// Default is 60.
    /// </summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the policy name.
    /// Default is "default".
    /// </summary>
    public string PolicyName { get; set; } = "default";

    /// <summary>
    /// Gets or sets whether to return 429 when quota is exhausted.
    /// Default is true.
    /// </summary>
    public bool Return429WhenExhausted { get; set; } = true;

    /// <summary>
    /// Gets or sets the Retry-After value in seconds when returning 429.
    /// Default is 60.
    /// </summary>
    public int RetryAfterSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets an optional delay before responding.
    /// </summary>
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Gets or sets an optional callback invoked for each request.
    /// Receives the HTTP context, request number, and remaining quota.
    /// </summary>
    public Action<HttpContext, int, int>? OnRequest { get; set; }

    /// <summary>
    /// Internal request counter for tracking requests across the server.
    /// </summary>
    internal int RequestCounter;
}
