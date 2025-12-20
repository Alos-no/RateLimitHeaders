using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RateLimitHeaders.Http;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Tests.Fixtures;

#pragma warning disable CA1859 // Use concrete types when possible for improved performance - intentional for interface testing

namespace RateLimitHeaders.Tests;

/// <summary>
/// Tests for IConfiguration and IOptions&lt;T&gt; support.
/// </summary>
public class ConfigurationTests
{
    #region IConfiguration Binding Tests

    [Fact]
    public void Handler_WithIConfiguration_ShouldBindOptions()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableProactiveThrottling"] = "false",
                ["QuotaLowThreshold"] = "0.25",
                ["TrackStatePerEndpoint"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddHttpClient("test")
            .AddRateLimitAwareHandler(config);

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        // Act - create client (this triggers handler creation)
        var client = factory.CreateClient("test");

        // Assert - client should be created successfully
        client.Should().NotBeNull();
    }

    [Fact]
    public async Task Handler_WithIConfigurationAndCallbacks_ShouldWork()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableProactiveThrottling"] = "false",
                ["QuotaLowThreshold"] = "0.15"
            })
            .Build();

        var receivedInfos = new List<RateLimitInfo>();

        await using var server = await RateLimitTestServer.CreateAsync(new RateLimitTestServerOptions
        {
            InitialQuota = 100,
            WindowSeconds = 60
        });

        var services = new ServiceCollection();
        services.AddHttpClient("test", client => client.BaseAddress = server.Client.BaseAddress)
            .ConfigurePrimaryHttpMessageHandler(() => server.Client.GetHandler()!)
            .AddRateLimitAwareHandler(config, options =>
            {
                options.OnRateLimitInfo = args => { receivedInfos.Add(args.RateLimitInfo); return ValueTask.CompletedTask; };
            });

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("test");

        // Act
        await client.GetAsync("/api/test");

        // Assert
        receivedInfos.Should().HaveCount(1);
    }

    #endregion

    #region IOptions<T> Pattern Tests

    [Fact]
    public async Task Handler_WithNamedOptions_ShouldResolveCorrectOptions()
    {
        // Arrange
        await using var server = await RateLimitTestServer.CreateAsync(new RateLimitTestServerOptions
        {
            InitialQuota = 100,
            WindowSeconds = 60
        });

        var receivedInfos = new List<RateLimitInfo>();

        var services = new ServiceCollection();

        // Configure named options
        services.Configure<RateLimitAwareOptions>("MyApi", options =>
        {
            options.EnableProactiveThrottling = false;
            options.QuotaLowThreshold = 0.2;
            options.OnRateLimitInfo = args => { receivedInfos.Add(args.RateLimitInfo); return ValueTask.CompletedTask; };
        });

        services.AddHttpClient("MyApi", client => client.BaseAddress = server.Client.BaseAddress)
            .ConfigurePrimaryHttpMessageHandler(() => server.Client.GetHandler()!)
            .AddRateLimitAwareHandler("MyApi");

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("MyApi");

        // Act
        await client.GetAsync("/api/test");

        // Assert
        receivedInfos.Should().HaveCount(1);
    }

    [Fact]
    public async Task Handler_WithNamedOptionsFromConfig_ShouldBindCorrectly()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MyApi:EnableProactiveThrottling"] = "false",
                ["MyApi:QuotaLowThreshold"] = "0.35"
            })
            .Build();

        await using var server = await RateLimitTestServer.CreateAsync(new RateLimitTestServerOptions
        {
            InitialQuota = 100
        });

        var receivedInfos = new List<RateLimitInfo>();

        var services = new ServiceCollection();

        // Bind from configuration section
        services.Configure<RateLimitAwareOptions>("MyApi", config.GetSection("MyApi"));

        // Also add a callback programmatically using PostConfigure
        services.PostConfigure<RateLimitAwareOptions>("MyApi", options =>
        {
            options.OnRateLimitInfo = args => { receivedInfos.Add(args.RateLimitInfo); return ValueTask.CompletedTask; };
        });

        services.AddHttpClient("MyApi", client => client.BaseAddress = server.Client.BaseAddress)
            .ConfigurePrimaryHttpMessageHandler(() => server.Client.GetHandler()!)
            .AddRateLimitAwareHandler("MyApi");

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("MyApi");

        // Act
        await client.GetAsync("/api/test");

        // Assert
        receivedInfos.Should().HaveCount(1);
    }

    [Fact]
    public void Handler_WithDifferentNamedOptions_ShouldUseCorrectOptions()
    {
        // Arrange
        var services = new ServiceCollection();

        var api1Throttling = false;
        var api2Throttling = true;

        services.Configure<RateLimitAwareOptions>("Api1", opt => opt.EnableProactiveThrottling = api1Throttling);
        services.Configure<RateLimitAwareOptions>("Api2", opt => opt.EnableProactiveThrottling = api2Throttling);

        services.AddHttpClient("Api1").AddRateLimitAwareHandler("Api1");
        services.AddHttpClient("Api2").AddRateLimitAwareHandler("Api2");

        var provider = services.BuildServiceProvider();
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<RateLimitAwareOptions>>();

        // Act
        var api1Options = optionsMonitor.Get("Api1");
        var api2Options = optionsMonitor.Get("Api2");

        // Assert
        api1Options.EnableProactiveThrottling.Should().Be(api1Throttling);
        api2Options.EnableProactiveThrottling.Should().Be(api2Throttling);
    }

    #endregion

    #region IRateLimitStateProvider Tests

    [Fact]
    public void StateProvider_GetAllStates_ShouldReturnAllTrackedStates()
    {
        // Arrange
        var stateTracker = new RateLimitStateTracker();

        stateTracker.UpdateState("endpoint1", new RateLimitInfo
        {
            PolicyName = "policy1",
            Remaining = 50,
            Quota = 100,
            IsValid = true
        });

        stateTracker.UpdateState("endpoint2", new RateLimitInfo
        {
            PolicyName = "policy2",
            Remaining = 25,
            Quota = 100,
            IsValid = true
        });

        // Act
        var provider = (Throttling.IRateLimitStateProvider)stateTracker;
        var allStates = provider.GetAllStates().ToList();

        // Assert
        allStates.Should().HaveCount(2);
        allStates.Select(s => s.PolicyName).Should().Contain("policy1").And.Contain("policy2");
    }

    [Fact]
    public void StateProvider_TrackedEndpoints_ShouldReturnAllKeys()
    {
        // Arrange
        var stateTracker = new RateLimitStateTracker();

        stateTracker.UpdateState("api.example.com/v1", new RateLimitInfo { IsValid = true });
        stateTracker.UpdateState("api.example.com/v2", new RateLimitInfo { IsValid = true });

        // Act
        var provider = (Throttling.IRateLimitStateProvider)stateTracker;
        var endpoints = provider.TrackedEndpoints.ToList();

        // Assert
        endpoints.Should().HaveCount(2);
        endpoints.Should().Contain("api.example.com/v1").And.Contain("api.example.com/v2");
    }

    [Fact]
    public void StateProvider_GetRateLimitInfo_ShouldReturnCorrectInfo()
    {
        // Arrange
        var stateTracker = new RateLimitStateTracker();
        var expectedInfo = new RateLimitInfo
        {
            PolicyName = "test-policy",
            Remaining = 75,
            Quota = 100,
            ResetSeconds = 30,
            IsValid = true
        };

        stateTracker.UpdateState("test-endpoint", expectedInfo);

        // Act
        var provider = (Throttling.IRateLimitStateProvider)stateTracker;
        var actualInfo = provider.GetRateLimitInfo("test-endpoint");

        // Assert
        actualInfo.PolicyName.Should().Be(expectedInfo.PolicyName);
        actualInfo.Remaining.Should().Be(expectedInfo.Remaining);
        actualInfo.Quota.Should().Be(expectedInfo.Quota);
    }

    [Fact]
    public void ThrottlingAlgorithm_WithStateProvider_ShouldReceiveProvider()
    {
        // Arrange
        Throttling.IRateLimitStateProvider? receivedProvider = null;
        var testAlgorithm = new TestThrottlingAlgorithmWithStateProvider(provider =>
        {
            receivedProvider = provider;
            return Throttling.ThrottlingResult.NoThrottle;
        });

        var stateTracker = new RateLimitStateTracker();
        stateTracker.UpdateState("test", new RateLimitInfo { IsValid = true, Remaining = 5, Quota = 100 });

        // Act
        var info = stateTracker.GetRateLimitInfo("test");
        testAlgorithm.Evaluate(info, stateTracker);

        // Assert
        receivedProvider.Should().NotBeNull();
        receivedProvider.Should().BeSameAs(stateTracker);
    }

    #endregion

    #region Helper Classes

    private sealed class TestThrottlingAlgorithmWithStateProvider : Throttling.IThrottlingAlgorithm
    {
        private readonly Func<Throttling.IRateLimitStateProvider?, Throttling.ThrottlingResult> _evaluator;

        public TestThrottlingAlgorithmWithStateProvider(Func<Throttling.IRateLimitStateProvider?, Throttling.ThrottlingResult> evaluator)
        {
            _evaluator = evaluator;
        }

        public Throttling.ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo) =>
            Evaluate(rateLimitInfo, null);

        public Throttling.ThrottlingResult Evaluate(RateLimitInfo rateLimitInfo, Throttling.IRateLimitStateProvider? stateProvider) =>
            _evaluator(stateProvider);
    }

    #endregion
}
