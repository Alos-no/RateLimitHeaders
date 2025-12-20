using Polly;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Polly;
using RateLimitHeaders.Tests.Fixtures;

namespace RateLimitHeaders.Polly.Tests;

/// <summary>
/// Integration tests for the Polly resilience strategy with test server.
/// </summary>
public class PollyIntegrationTests
{
    [Fact]
    public async Task PollyPipeline_WithTestServer_ShouldParseHeaders()
    {
        // Arrange
        await using var server = await RateLimitTestServer.CreateAsync(new RateLimitTestServerOptions
        {
            InitialQuota = 100,
            WindowSeconds = 60
        });

        RateLimitInfo? capturedInfo = null;
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
                options.OnRateLimitInfo = args =>
                {
                    capturedInfo = args.RateLimitInfo;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        // Act
        var context = ResilienceContextPool.Shared.Get();
        var response = await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("/api/test", ctx.CancellationToken),
            context,
            server.Client);

        // Assert
        capturedInfo.Should().NotBeNull();
        capturedInfo!.Value.IsValid.Should().BeTrue();
        capturedInfo.Value.Remaining.Should().Be(99);

        // Check that info is stored in context properties
        context.Properties.TryGetValue(RateLimitContextProperties.RateLimitInfoKey, out var storedInfo);
        storedInfo.Should().Be(capturedInfo.Value);

        ResilienceContextPool.Shared.Return(context);
    }
}
