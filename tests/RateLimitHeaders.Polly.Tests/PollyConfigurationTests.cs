using System.Net;
using Microsoft.Extensions.Configuration;
using Polly;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Polly;
using RateLimitHeaders.Tests.Fixtures;

namespace RateLimitHeaders.Polly.Tests;

/// <summary>
/// Tests for IConfiguration support in the Polly resilience strategy.
/// </summary>
public class PollyConfigurationTests
{
    [Fact]
    public void Strategy_WithIConfiguration_ShouldBindOptions()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableProactiveThrottling"] = "false",
                ["QuotaLowThreshold"] = "0.3",
                ["TrackStatePerEndpoint"] = "true"
            })
            .Build();

        // Act
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(config)
            .Build();

        // Assert
        pipeline.Should().NotBeNull();
    }

    [Fact]
    public async Task Strategy_WithIConfigurationAndCallbacks_ShouldWork()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableProactiveThrottling"] = "false"
            })
            .Build();

        RateLimitHeaders.Parsing.RateLimitInfo? capturedInfo = null;

        await using var server = await RateLimitTestServer.CreateAsync(new RateLimitTestServerOptions
        {
            InitialQuota = 100
        });

        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(config, options =>
            {
                options.OnRateLimitInfo = args =>
                {
                    capturedInfo = args.RateLimitInfo;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        // Act
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            async (ctx) => await server.Client.GetAsync("/api/test", ctx.CancellationToken),
            context);
        ResilienceContextPool.Shared.Return(context);

        // Assert
        capturedInfo.Should().NotBeNull();
        capturedInfo!.Value.IsValid.Should().BeTrue();
    }

    #region Configuration Binding Edge Cases

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.1")]
    [InlineData("2.0")]
    public void Strategy_WithInvalidQuotaLowThreshold_ShouldThrowOnBuild(string invalidThreshold)
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableProactiveThrottling"] = "false",
                ["QuotaLowThreshold"] = invalidThreshold
            })
            .Build();

        // Act & Assert - Should throw during configuration binding
        // The ArgumentOutOfRangeException is wrapped in TargetInvocationException due to reflection
        var action = () => new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(config)
            .Build();

        action.Should().Throw<Exception>()
            .WithInnerException<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("0.0")]
    [InlineData("0.5")]
    [InlineData("1.0")]
    public void Strategy_WithValidQuotaLowThreshold_ShouldBindSuccessfully(string validThreshold)
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableProactiveThrottling"] = "false",
                ["QuotaLowThreshold"] = validThreshold
            })
            .Build();

        // Act
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(config)
            .Build();

        // Assert
        pipeline.Should().NotBeNull();
    }

    [Fact]
    public void Strategy_WithEmptyConfiguration_ShouldUseDefaults()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        // Act
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(config)
            .Build();

        // Assert - Should use defaults without throwing
        pipeline.Should().NotBeNull();
    }

    [Fact]
    public void Strategy_WithMalformedBooleanValue_ShouldThrow()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableProactiveThrottling"] = "not-a-boolean"
            })
            .Build();

        // Act & Assert - Configuration binding throws on invalid boolean
        var action = () => new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(config)
            .Build();

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Failed to convert configuration value*");
    }

    [Fact]
    public void Strategy_WithNonNumericQuotaLowThreshold_ShouldThrow()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["QuotaLowThreshold"] = "invalid-number"
            })
            .Build();

        // Act & Assert - Configuration binding throws on invalid number
        var action = () => new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(config)
            .Build();

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Failed to convert configuration value*");
    }

    [Fact]
    public async Task Strategy_WithConfigurationAndCallbackOverride_CallbackShouldTakePrecedence()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableProactiveThrottling"] = "false",
                ["QuotaLowThreshold"] = "0.5" // Config says 50%
            })
            .Build();

        OnQuotaLowArguments? receivedArgs = null;

        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(config, options =>
            {
                options.QuotaLowThreshold = 0.3; // Override to 30%
                options.OnQuotaLow = args =>
                {
                    receivedArgs = args;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var mockHandler = new MockHttpHandler();
        mockHandler.QueueRateLimitResponse(25, 30, 100, 60); // 25% - below 30% override but above 50% config
        var client = new HttpClient(mockHandler);

        // Act
        var context = ResilienceContextPool.Shared.Get();
        await pipeline.ExecuteAsync(
            static async (ctx, client) => await client.GetAsync("http://example.com/api/test", ctx.CancellationToken),
            context,
            client);
        ResilienceContextPool.Shared.Return(context);

        // Assert - Callback override should take precedence
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Value.Threshold.Should().Be(0.3);
    }

    #endregion
}
