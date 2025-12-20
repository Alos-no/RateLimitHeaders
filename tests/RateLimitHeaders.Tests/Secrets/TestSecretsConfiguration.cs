using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace RateLimitHeaders.Tests.Secrets;

/// <summary>
/// Provides a centralized configuration source for integration tests that loads settings from
/// multiple sources including appsettings.json, environment variables, and user secrets.
/// </summary>
/// <remarks>
/// <para>
/// Configuration sources are loaded in the following order (later sources override earlier ones):
/// <list type="number">
///   <item><description>appsettings.json (optional, if present in the test output directory)</description></item>
///   <item><description>Environment variables</description></item>
///   <item><description>User secrets (from test project assembly)</description></item>
/// </list>
/// </para>
/// <para>
/// To configure user secrets, run the setup script: <c>pwsh -File ./scripts/setup-test-secrets.ps1</c>
/// </para>
/// </remarks>
public static class TestSecretsConfiguration
{
    /// <summary>
    /// Gets the lazily-initialized configuration root that includes all configuration sources.
    /// </summary>
    public static IConfigurationRoot Configuration { get; }

    /// <summary>
    /// Gets the Cloudflare settings bound from the configuration. These settings are required
    /// for integration tests that interact with real Cloudflare APIs to validate rate limit header parsing.
    /// </summary>
    public static TestCloudflareSettings CloudflareSettings { get; }

    /// <summary>
    /// Initializes the static configuration by building configuration from all sources.
    /// </summary>
    static TestSecretsConfiguration()
    {
        var builder = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables();

        // Dynamically find the assembly containing the user secrets. This is necessary because
        // the UserSecretsId is defined in the test project. We scan the loaded assemblies to
        // find one that has the attribute and use it as the anchor.
        var testAssemblyWithSecrets = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetCustomAttribute<UserSecretsIdAttribute>() != null);

        if (testAssemblyWithSecrets != null)
        {
            builder.AddUserSecrets(testAssemblyWithSecrets);
        }

        Configuration = builder.Build();

        // Bind configuration sections to strongly-typed settings objects.
        CloudflareSettings = new TestCloudflareSettings();
        Configuration.GetSection(TestSecretKeys.Cloudflare.SectionName).Bind(CloudflareSettings);
    }

    /// <summary>
    /// Indicates whether the Cloudflare API token is configured and available for testing.
    /// </summary>
    public static bool IsCloudflareConfigured =>
        !string.IsNullOrWhiteSpace(CloudflareSettings.ApiToken);
}

/// <summary>
/// Represents the Cloudflare API configuration settings required for integration tests.
/// </summary>
public sealed class TestCloudflareSettings
{
    /// <summary>
    /// The Cloudflare API Token with permissions to call the API.
    /// Any valid token will work since we only need to receive rate limit headers.
    /// </summary>
    public string? ApiToken { get; set; }
}
