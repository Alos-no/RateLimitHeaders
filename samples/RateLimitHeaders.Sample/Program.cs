using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using RateLimitHeaders.Events;
using RateLimitHeaders.Http;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Polly;
using RateLimitHeaders.Sample;

// Run all sample scenarios
await SampleRunner.RunAllExamplesAsync();

namespace RateLimitHeaders.Sample
{
    /// <summary>
    /// Orchestrates the execution of all sample scenarios.
    /// </summary>
    public static class SampleRunner
    {
        /// <summary>
        /// Runs all sample examples in sequence.
        /// </summary>
        public static async Task RunAllExamplesAsync()
        {
            PrintHeader("RateLimitHeaders Sample Application");

            await ManualParsingExample.RunAsync();
            await HttpClientFactoryExample.RunAsync();
            await PollyPipelineExample.RunAsync();
            await MultiplePoliciesExample.RunAsync();
            ParameterOrderExample.Run();

            PrintSection("All samples completed successfully!");
        }

        /// <summary>
        /// Prints a formatted header to the console.
        /// </summary>
        public static void PrintHeader(string title)
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 60));
            Console.WriteLine($"  {title}");
            Console.WriteLine(new string('=', 60));
            Console.WriteLine();
        }

        /// <summary>
        /// Prints a formatted section header to the console.
        /// </summary>
        public static void PrintSection(string title)
        {
            Console.WriteLine();
            Console.WriteLine($">> {title}");
            Console.WriteLine(new string('-', 50));
        }

        /// <summary>
        /// Prints an indented info line.
        /// </summary>
        public static void PrintInfo(string message) => Console.WriteLine($"   {message}");

        /// <summary>
        /// Prints an indented callback event line.
        /// </summary>
        public static void PrintCallback(string eventName, string message)
            => Console.WriteLine($"   [{eventName}] {message}");
    }

    // =============================================================================
    // Example 1: Manual Header Parsing
    // =============================================================================
    // This example shows how to directly parse rate limit headers from an HTTP
    // response without using any middleware or handlers.
    // =============================================================================

    /// <summary>
    /// Demonstrates manual parsing of IETF RateLimit headers.
    /// </summary>
    /// <remarks>
    /// Use this approach when you need direct access to rate limit information
    /// without the overhead of handlers or middleware. This is useful for:
    /// <list type="bullet">
    ///   <item>Custom rate limiting logic</item>
    ///   <item>Logging and monitoring</item>
    ///   <item>Testing and debugging</item>
    /// </list>
    /// </remarks>
    public static class ManualParsingExample
    {
        public static Task RunAsync()
        {
            SampleRunner.PrintSection("Example 1: Manual Header Parsing");

            // Simulate an HTTP response with IETF RateLimit headers
            // In real usage, this would come from an actual API call
            var response = CreateSimulatedResponse();

            // Parse the headers using TryParse (recommended for production)
            if (RateLimitHeaderParser.TryParse(response, out var info))
            {
                DisplayRateLimitInfo(info);
            }
            else
            {
                SampleRunner.PrintInfo("No rate limit headers found in response");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Creates a simulated HTTP response with rate limit headers.
        /// </summary>
        /// <remarks>
        /// Header format follows IETF draft-ietf-httpapi-ratelimit-headers:
        /// <code>
        /// RateLimit: "policy-name";r=remaining;t=reset_seconds
        /// RateLimit-Policy: "policy-name";q=quota;w=window_seconds;pk=partition;qu=unit
        /// </code>
        /// </remarks>
        private static HttpResponseMessage CreateSimulatedResponse()
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);

            // RateLimit header: current state (95 remaining, resets in 58 seconds)
            response.Headers.Add("RateLimit", "\"api-v2\";r=95;t=58");

            // RateLimit-Policy header: policy definition with optional parameters
            // - q=100: quota of 100 requests
            // - w=60: 60 second window
            // - pk="tenant-123": partition key for multi-tenant scenarios
            // - qu=requests: quota unit (requests, bytes, etc.)
            response.Headers.Add("RateLimit-Policy", "\"api-v2\";q=100;w=60;pk=\"tenant-123\";qu=requests");

            return response;
        }

        /// <summary>
        /// Displays parsed rate limit information to the console.
        /// </summary>
        private static void DisplayRateLimitInfo(RateLimitInfo info)
        {
            SampleRunner.PrintInfo($"Policy Name:    {info.PolicyName}");
            SampleRunner.PrintInfo($"Remaining:      {info.Remaining} / {info.Quota}");
            SampleRunner.PrintInfo($"Usage:          {info.GetRemainingPercentage():P0} remaining");
            SampleRunner.PrintInfo($"Reset In:       {info.ResetSeconds} seconds");
            SampleRunner.PrintInfo($"Window:         {info.WindowSeconds} seconds");
            SampleRunner.PrintInfo($"Is Quota Low:   {info.IsQuotaLow(threshold: 0.1)} (at 10% threshold)");

            // Display optional IETF parameters if present
            if (info.PartitionKey is not null)
            {
                SampleRunner.PrintInfo($"Partition Key:  {info.PartitionKey}");
            }
            if (info.QuotaUnit is not null)
            {
                SampleRunner.PrintInfo($"Quota Unit:     {info.QuotaUnit}");
            }
        }
    }

    // =============================================================================
    // Example 2: IHttpClientFactory Integration
    // =============================================================================
    // This example shows how to integrate rate limit awareness with
    // IHttpClientFactory using the AddRateLimitAwareHandler extension method.
    // =============================================================================

    /// <summary>
    /// Demonstrates integration with IHttpClientFactory using a DelegatingHandler.
    /// </summary>
    /// <remarks>
    /// This is the recommended approach for most applications using HttpClient.
    /// The handler automatically:
    /// <list type="bullet">
    ///   <item>Parses rate limit headers from every response</item>
    ///   <item>Tracks rate limit state per endpoint</item>
    ///   <item>Proactively throttles requests when quota is low</item>
    ///   <item>Respects Retry-After headers on 429/503 responses</item>
    /// </list>
    /// </remarks>
    public static class HttpClientFactoryExample
    {
        public static async Task RunAsync()
        {
            SampleRunner.PrintSection("Example 2: IHttpClientFactory Integration");

            // Build the service provider with configured HTTP client
            await using var provider = BuildServiceProvider();

            // Get the configured HTTP client
            var factory = provider.GetRequiredService<IHttpClientFactory>();
            var client = factory.CreateClient("RateLimitedApi");

            // Make a test request
            SampleRunner.PrintInfo("Making request to httpbin.org...");
            SampleRunner.PrintInfo("(Note: httpbin.org doesn't return rate limit headers,");
            SampleRunner.PrintInfo(" but the handler is configured and ready)");

            try
            {
                var response = await client.GetAsync("get");
                SampleRunner.PrintInfo($"Response Status: {response.StatusCode}");
            }
            catch (HttpRequestException ex)
            {
                SampleRunner.PrintInfo($"Request failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds a service provider with a rate-limit-aware HTTP client.
        /// </summary>
        private static ServiceProvider BuildServiceProvider()
        {
            var services = new ServiceCollection();

            // Add logging (optional but recommended for debugging)
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });

            // Configure the HTTP client with rate limit awareness
            services.AddHttpClient("RateLimitedApi", ConfigureHttpClient)
                .AddRateLimitAwareHandler(ConfigureRateLimitHandler);

            return services.BuildServiceProvider();
        }

        /// <summary>
        /// Configures the base HTTP client settings.
        /// </summary>
        private static void ConfigureHttpClient(HttpClient client)
        {
            client.BaseAddress = new Uri("https://httpbin.org/");
            client.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Configures the rate limit aware handler options.
        /// </summary>
        private static void ConfigureRateLimitHandler(RateLimitAwareOptions options)
        {
            // Enable proactive throttling to delay requests when quota is low
            options.EnableProactiveThrottling = true;

            // Set the threshold at which to start warning about low quota
            options.QuotaLowThreshold = 0.2; // 20% remaining

            // Custom state key extractor for partition-aware tracking
            // This allows tracking rate limits separately per API key, tenant, etc.
            options.StateKeyExtractor = ExtractStateKey;

            // Configure callbacks for rate limit events
            options.OnRateLimitInfo = HandleRateLimitInfo;
            options.OnQuotaLow = HandleQuotaLow;
            options.OnThrottling = HandleThrottling;
        }

        /// <summary>
        /// Extracts a state key from the request for per-endpoint tracking.
        /// </summary>
        /// <remarks>
        /// This example uses a combination of host and API key to track
        /// rate limits separately for each API key. Customize this for
        /// your specific partitioning needs.
        /// </remarks>
        private static string ExtractStateKey(HttpRequestMessage request)
        {
            var host = request.RequestUri?.Host ?? "default";

            // Check for an API key header (common pattern)
            if (request.Headers.TryGetValues("X-API-Key", out var values))
            {
                var apiKey = values.FirstOrDefault();
                if (apiKey is not null)
                {
                    return $"{host}:{apiKey}";
                }
            }

            return host;
        }

        /// <summary>
        /// Handles the OnRateLimitInfo callback when rate limit headers are parsed.
        /// </summary>
        private static ValueTask HandleRateLimitInfo(RateLimitEventArgs args)
        {
            SampleRunner.PrintCallback("RateLimitInfo",
                $"Remaining: {args.RateLimitInfo.Remaining}/{args.RateLimitInfo.Quota}");

            if (args.RateLimitInfo.PartitionKey is not null)
            {
                SampleRunner.PrintInfo($"       Partition: {args.RateLimitInfo.PartitionKey}");
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Handles the OnQuotaLow callback when quota falls below threshold.
        /// </summary>
        private static ValueTask HandleQuotaLow(QuotaLowEventArgs args)
        {
            SampleRunner.PrintCallback("QuotaLow",
                $"Warning! Only {args.RemainingPercentage:P0} of quota remaining");
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Handles the OnThrottling callback when a request is being delayed.
        /// </summary>
        private static ValueTask HandleThrottling(ThrottlingEventArgs args)
        {
            SampleRunner.PrintCallback("Throttling",
                $"Delaying request for {args.Delay.TotalMilliseconds:F0}ms - {args.Reason}");
            return ValueTask.CompletedTask;
        }
    }

    // =============================================================================
    // Example 3: Polly Resilience Pipeline
    // =============================================================================
    // This example shows how to use the rate limit headers strategy as part of
    // a Polly resilience pipeline, giving you access to rate limit info in the
    // ResilienceContext for custom handling.
    // =============================================================================

    /// <summary>
    /// Demonstrates integration with Polly's resilience pipeline system.
    /// </summary>
    /// <remarks>
    /// Use this approach when you want to:
    /// <list type="bullet">
    ///   <item>Combine rate limit awareness with other Polly strategies (retry, circuit breaker)</item>
    ///   <item>Access rate limit info in the ResilienceContext for custom logic</item>
    ///   <item>Use Polly's telemetry system for monitoring</item>
    /// </list>
    /// </remarks>
    public static class PollyPipelineExample
    {
        public static async Task RunAsync()
        {
            SampleRunner.PrintSection("Example 3: Polly Resilience Pipeline");

            // Build the resilience pipeline
            var pipeline = BuildPipeline();

            // Create a simulated response
            var response = CreateSimulatedResponse();

            // Execute through the pipeline
            SampleRunner.PrintInfo("Executing request through resilience pipeline...");

            var context = ResilienceContextPool.Shared.Get();
            try
            {
                var result = await pipeline.ExecuteAsync(
                    static (ctx, resp) => ValueTask.FromResult(resp),
                    context,
                    response);

                // Access rate limit info from context (stored by the strategy)
                if (context.Properties.TryGetValue(RateLimitContextProperties.RateLimitInfoKey, out var storedInfo))
                {
                    SampleRunner.PrintInfo($"Rate limit info stored in context:");
                    SampleRunner.PrintInfo($"  Policy: {storedInfo.PolicyName}");
                    SampleRunner.PrintInfo($"  Remaining: {storedInfo.Remaining}/{storedInfo.Quota}");
                }
            }
            finally
            {
                ResilienceContextPool.Shared.Return(context);
            }
        }

        /// <summary>
        /// Builds a resilience pipeline with rate limit header parsing.
        /// </summary>
        private static ResiliencePipeline<HttpResponseMessage> BuildPipeline()
        {
            return new ResiliencePipelineBuilder<HttpResponseMessage>()
                .AddRateLimitHeaders(options =>
                {
                    // Disable proactive throttling - just parse and store info
                    options.EnableProactiveThrottling = false;

                    // Log when rate limit info is parsed
                    options.OnRateLimitInfo = args =>
                    {
                        SampleRunner.PrintCallback("Pipeline",
                            $"Parsed rate limit: {args.RateLimitInfo.PolicyName}");
                        return ValueTask.CompletedTask;
                    };
                })
                .Build();
        }

        private static HttpResponseMessage CreateSimulatedResponse()
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            response.Headers.Add("RateLimit", "\"pipeline-demo\";r=42;t=120");
            response.Headers.Add("RateLimit-Policy", "\"pipeline-demo\";q=100;w=300");
            return response;
        }
    }

    // =============================================================================
    // Example 4: Multiple Rate Limit Policies
    // =============================================================================
    // Many APIs expose multiple rate limit policies (e.g., burst and daily limits).
    // The parser automatically selects the most restrictive policy.
    // =============================================================================

    /// <summary>
    /// Demonstrates handling of multiple rate limit policies.
    /// </summary>
    /// <remarks>
    /// When an API returns multiple policies, the parser:
    /// <list type="bullet">
    ///   <item>Parses all policies from the headers</item>
    ///   <item>Matches RateLimit entries with RateLimit-Policy entries by name</item>
    ///   <item>Returns the most restrictive policy (lowest remaining percentage)</item>
    /// </list>
    /// </remarks>
    public static class MultiplePoliciesExample
    {
        public static Task RunAsync()
        {
            SampleRunner.PrintSection("Example 4: Multiple Rate Limit Policies");

            // Create a response with multiple policies
            var response = CreateMultiPolicyResponse();

            SampleRunner.PrintInfo("Response contains two policies:");
            SampleRunner.PrintInfo("  - 'burst':  10/100 remaining (10%) - short window");
            SampleRunner.PrintInfo("  - 'daily': 900/1000 remaining (90%) - long window");
            SampleRunner.PrintInfo("");

            if (RateLimitHeaderParser.TryParse(response, out var info))
            {
                SampleRunner.PrintInfo($"Most restrictive policy selected: '{info.PolicyName}'");
                SampleRunner.PrintInfo($"  Remaining: {info.Remaining}/{info.Quota} ({info.GetRemainingPercentage():P0})");
                SampleRunner.PrintInfo($"  Reset in: {info.ResetSeconds} seconds");
                SampleRunner.PrintInfo($"  Window: {info.WindowSeconds} seconds");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Creates a response with multiple rate limit policies.
        /// </summary>
        /// <remarks>
        /// This simulates an API with both burst (short-term) and daily (long-term) limits.
        /// The burst policy is more restrictive (10% remaining vs 90%) so it will be selected.
        /// </remarks>
        private static HttpResponseMessage CreateMultiPolicyResponse()
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);

            // Multiple policies in a single header, comma-separated
            response.Headers.Add("RateLimit",
                "\"burst\";r=10;t=30,\"daily\";r=900;t=43200");

            response.Headers.Add("RateLimit-Policy",
                "\"burst\";q=100;w=60,\"daily\";q=1000;w=86400");

            return response;
        }
    }

    // =============================================================================
    // Example 5: Parameter Order Independence (RFC 8941)
    // =============================================================================
    // Per RFC 8941 (Structured Field Values), parameters can appear in any order.
    // The parser correctly handles both "r=50;t=30" and "t=30;r=50".
    // =============================================================================

    /// <summary>
    /// Demonstrates RFC 8941 compliant parameter order independence.
    /// </summary>
    /// <remarks>
    /// The IETF spec allows parameters to appear in any order.
    /// Both of these are equivalent and valid:
    /// <code>
    /// RateLimit: "policy";r=50;t=30
    /// RateLimit: "policy";t=30;r=50
    /// </code>
    /// </remarks>
    public static class ParameterOrderExample
    {
        public static void Run()
        {
            SampleRunner.PrintSection("Example 5: Parameter Order Independence (RFC 8941)");

            // Standard order: r before t
            var standardOrder = ParseHeader("\"api\";r=50;t=30");

            // Reversed order: t before r (equally valid per RFC 8941)
            var reversedOrder = ParseHeader("\"api\";t=30;r=50");

            SampleRunner.PrintInfo("Standard order (r=50;t=30):");
            SampleRunner.PrintInfo($"  Remaining: {standardOrder.Remaining}, Reset: {standardOrder.ResetSeconds}s");

            SampleRunner.PrintInfo("Reversed order (t=30;r=50):");
            SampleRunner.PrintInfo($"  Remaining: {reversedOrder.Remaining}, Reset: {reversedOrder.ResetSeconds}s");

            SampleRunner.PrintInfo("");
            SampleRunner.PrintInfo($"Both parse identically: {standardOrder.Remaining == reversedOrder.Remaining}");
        }

        private static RateLimitInfo ParseHeader(string headerValue)
        {
            return RateLimitHeaderParser.Parse(headerValue, rateLimitPolicyHeaderValue: null);
        }
    }
}
