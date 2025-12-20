using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RateLimitHeaders.Http;
using RateLimitHeaders.Tests.Fixtures;

namespace RateLimitHeaders.Tests;

/// <summary>
/// Tests for ServiceCollectionExtensions DI registration.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    #region Handler Registration Tests

    [Fact]
    public void AddRateLimitAwareHandler_WithDefaults_ShouldRegisterTransientHandler()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient("test")
            .AddRateLimitAwareHandler();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        // Act
        var client1 = factory.CreateClient("test");
        var client2 = factory.CreateClient("test");

        // Assert
        client1.Should().NotBeNull();
        client2.Should().NotBeNull();
        // Transient handlers are pooled by HttpClientFactory, so we can't directly test
        // transient registration, but we verify clients are created successfully
    }

    [Fact]
    public async Task AddRateLimitAwareHandler_WithOptionsAction_ShouldApplyOptions()
    {
        // Arrange
        await using var server = await RateLimitTestServer.CreateAsync(new RateLimitTestServerOptions
        {
            InitialQuota = 100,
            WindowSeconds = 60
        });

        var quotaLowCallbackInvoked = false;
        var customThreshold = 0.25;

        var services = new ServiceCollection();
        services.AddHttpClient("test", client => client.BaseAddress = server.Client.BaseAddress)
            .ConfigurePrimaryHttpMessageHandler(() => server.Client.GetHandler()!)
            .AddRateLimitAwareHandler(options =>
            {
                options.QuotaLowThreshold = customThreshold;
                options.EnableProactiveThrottling = false; // Disable to avoid timing issues
                options.OnQuotaLow = _ =>
                {
                    quotaLowCallbackInvoked = true;
                    return ValueTask.CompletedTask;
                };
            });

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("test");

        // Act - Make requests to verify options were applied
        // With QuotaLowThreshold = 0.25, callback should fire at 25% remaining (25/100)
        for (int i = 0; i < 76; i++) // Get to 24% remaining (24/100)
        {
            await client.GetAsync("/api/test");
        }

        // Assert
        quotaLowCallbackInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task AddRateLimitAwareHandler_WithConfiguration_ShouldBindValues()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableProactiveThrottling"] = "false",
                ["QuotaLowThreshold"] = "0.5", // 50% threshold
                ["TrackStatePerEndpoint"] = "true"
            })
            .Build();

        await using var server = await RateLimitTestServer.CreateAsync(new RateLimitTestServerOptions
        {
            InitialQuota = 100,
            WindowSeconds = 60
        });

        var quotaLowCallbackInvoked = false;
        var services = new ServiceCollection();
        services.AddHttpClient("test", client => client.BaseAddress = server.Client.BaseAddress)
            .ConfigurePrimaryHttpMessageHandler(() => server.Client.GetHandler()!)
            .AddRateLimitAwareHandler(config, options =>
            {
                options.OnQuotaLow = _ =>
                {
                    quotaLowCallbackInvoked = true;
                    return ValueTask.CompletedTask;
                };
            });

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("test");

        // Act - Make requests to trigger quota low at 50%
        for (int i = 0; i < 51; i++) // Get to 49% remaining (49/100)
        {
            await client.GetAsync("/api/test");
        }

        // Assert
        quotaLowCallbackInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task AddRateLimitAwareHandler_WithConfigurationAndCallback_ShouldMerge()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableProactiveThrottling"] = "false",
                ["QuotaLowThreshold"] = "0.5"
            })
            .Build();

        await using var server = await RateLimitTestServer.CreateAsync(new RateLimitTestServerOptions
        {
            InitialQuota = 100,
            WindowSeconds = 60
        });

        var rateLimitInfoReceived = false;
        var quotaLowReceived = false;

        var services = new ServiceCollection();
        services.AddHttpClient("test", client => client.BaseAddress = server.Client.BaseAddress)
            .ConfigurePrimaryHttpMessageHandler(() => server.Client.GetHandler()!)
            .AddRateLimitAwareHandler(config, options =>
            {
                // Callback set programmatically
                options.OnRateLimitInfo = _ =>
                {
                    rateLimitInfoReceived = true;
                    return ValueTask.CompletedTask;
                };
                options.OnQuotaLow = _ =>
                {
                    quotaLowReceived = true;
                    return ValueTask.CompletedTask;
                };
            });

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("test");

        // Act
        for (int i = 0; i < 51; i++)
        {
            await client.GetAsync("/api/test");
        }

        // Assert - Both configuration (threshold) and callback should be applied
        rateLimitInfoReceived.Should().BeTrue();
        quotaLowReceived.Should().BeTrue();
    }

    [Fact]
    public void AddRateLimitAwareHandler_WithNamedOptions_ShouldResolveByName()
    {
        // Arrange
        var services = new ServiceCollection();

        services.Configure<RateLimitAwareOptions>("Api1", opt => opt.QuotaLowThreshold = 0.1);
        services.Configure<RateLimitAwareOptions>("Api2", opt => opt.QuotaLowThreshold = 0.5);

        services.AddHttpClient("Api1").AddRateLimitAwareHandler("Api1");
        services.AddHttpClient("Api2").AddRateLimitAwareHandler("Api2");

        var provider = services.BuildServiceProvider();
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<RateLimitAwareOptions>>();

        // Act
        var api1Options = optionsMonitor.Get("Api1");
        var api2Options = optionsMonitor.Get("Api2");

        // Assert
        api1Options.QuotaLowThreshold.Should().Be(0.1);
        api2Options.QuotaLowThreshold.Should().Be(0.5);
    }

    [Fact]
    public void AddRateLimitAwareHandler_MultipleTimes_ShouldNotConflict()
    {
        // Arrange
        var services = new ServiceCollection();

        services.AddHttpClient("client1")
            .AddRateLimitAwareHandler(opt => opt.EnableProactiveThrottling = false);
        services.AddHttpClient("client2")
            .AddRateLimitAwareHandler(opt => opt.EnableProactiveThrottling = true);

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        // Act
        var client1 = factory.CreateClient("client1");
        var client2 = factory.CreateClient("client2");

        // Assert
        client1.Should().NotBeNull();
        client2.Should().NotBeNull();
    }

    #endregion

    #region Options Validation Tests

    [Fact]
    public void RateLimitAwareOptions_QuotaLowThreshold_ShouldAcceptValidRange()
    {
        // Arrange
        var options = new RateLimitAwareOptions();

        // Act & Assert - Valid values
        options.QuotaLowThreshold = 0.0;
        options.QuotaLowThreshold.Should().Be(0.0);

        options.QuotaLowThreshold = 0.5;
        options.QuotaLowThreshold.Should().Be(0.5);

        options.QuotaLowThreshold = 1.0;
        options.QuotaLowThreshold.Should().Be(1.0);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(-1.0)]
    [InlineData(2.0)]
    public void RateLimitAwareOptions_QuotaLowThreshold_ShouldRejectInvalidValues(double invalidValue)
    {
        // Arrange
        var options = new RateLimitAwareOptions();

        // Act & Assert
        var act = () => options.QuotaLowThreshold = invalidValue;
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RateLimitAwareOptions_DefaultValues_ShouldBeCorrect()
    {
        // Arrange & Act
        var options = new RateLimitAwareOptions();

        // Assert
        options.EnableProactiveThrottling.Should().BeTrue();
        options.QuotaLowThreshold.Should().Be(0.1);
        options.TrackStatePerEndpoint.Should().BeTrue();
        options.ThrottlingAlgorithm.Should().NotBeNull();
        options.OnRateLimitInfo.Should().BeNull();
        options.OnQuotaLow.Should().BeNull();
        options.OnThrottling.Should().BeNull();
        options.StateKeyExtractor.Should().BeNull();
    }

    #endregion

    #region Integration with IHttpClientFactory Tests

    [Fact]
    public async Task Handler_IntegratedWithFactory_ShouldParseHeaders()
    {
        // Arrange
        await using var server = await RateLimitTestServer.CreateAsync(new RateLimitTestServerOptions
        {
            InitialQuota = 100,
            WindowSeconds = 60
        });

        var receivedInfos = new List<Parsing.RateLimitInfo>();

        var services = new ServiceCollection();
        services.AddHttpClient("test", client => client.BaseAddress = server.Client.BaseAddress)
            .ConfigurePrimaryHttpMessageHandler(() => server.Client.GetHandler()!)
            .AddRateLimitAwareHandler(options =>
            {
                options.EnableProactiveThrottling = false;
                options.OnRateLimitInfo = args =>
                {
                    receivedInfos.Add(args.RateLimitInfo);
                    return ValueTask.CompletedTask;
                };
            });

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("test");

        // Act
        await client.GetAsync("/api/test");
        await client.GetAsync("/api/test");

        // Assert
        receivedInfos.Should().HaveCount(2);
        receivedInfos[0].Remaining.Should().Be(99);
        receivedInfos[1].Remaining.Should().Be(98);
    }

    #endregion
}
