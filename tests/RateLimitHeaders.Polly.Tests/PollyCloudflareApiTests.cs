using Polly;
using RateLimitHeaders.Polly;
using RateLimitHeaders.Tests.Secrets;

namespace RateLimitHeaders.Polly.Tests;

/// <summary>
/// Integration tests that validate the Polly resilience strategy against the real Cloudflare API.
/// These tests require a valid Cloudflare API token configured via user secrets.
/// </summary>
/// <remarks>
/// <para>
/// To configure the API token, run: <c>pwsh -File ./scripts/setup-test-secrets.ps1</c>
/// </para>
/// </remarks>
public class PollyCloudflareApiTests : IDisposable
{
    private readonly bool _isConfigured;

    // Endpoint that returns rate limit headers
    private const string TestEndpoint = "zones";

    public PollyCloudflareApiTests()
    {
        _isConfigured = TestSecretsConfiguration.IsCloudflareConfigured;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private HttpClient CreateConfiguredClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/")
        };

        if (_isConfigured)
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestSecretsConfiguration.CloudflareSettings.ApiToken);
        }

        return client;
    }

    private void SkipIfNotConfigured()
    {
        Assert.True(_isConfigured, "Cloudflare API token not configured. Run scripts/setup-test-secrets.ps1");
    }

    [Fact]
    public async Task CloudflareApi_PollyPipeline_ShouldStoreInfoInContext()
    {
        SkipIfNotConfigured();

        // Arrange
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitHeaders(options =>
            {
                options.EnableProactiveThrottling = false;
            })
            .Build();

        using var client = CreateConfiguredClient();

        // Act
        var context = ResilienceContextPool.Shared.Get();
        try
        {
            var response = await pipeline.ExecuteAsync(
                async (ctx, httpClient) => await httpClient.GetAsync(TestEndpoint, ctx.CancellationToken),
                context,
                client);

            Console.WriteLine($"Response Status: {response.StatusCode}");

            // Assert - Rate limit info should be stored in context properties
            var hasStoredInfo = context.Properties.TryGetValue(
                RateLimitContextProperties.RateLimitInfoKey, out var storedInfo);

            hasStoredInfo.Should().BeTrue("Rate limit info should be stored in context properties");
            storedInfo.IsValid.Should().BeTrue();

            Console.WriteLine($"Context stored: Policy={storedInfo.PolicyName}, Remaining={storedInfo.Remaining}");
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }
}
