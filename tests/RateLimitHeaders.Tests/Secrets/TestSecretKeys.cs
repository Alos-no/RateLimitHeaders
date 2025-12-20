namespace RateLimitHeaders.Tests.Secrets;

/// <summary>
/// Constants for configuration key paths used in integration tests.
/// </summary>
public static class TestSecretKeys
{
    /// <summary>
    /// Configuration keys for Cloudflare API settings.
    /// </summary>
    public static class Cloudflare
    {
        /// <summary>The configuration section name for Cloudflare settings.</summary>
        public const string SectionName = "Cloudflare";

        /// <summary>The Cloudflare API Token configuration key.</summary>
        public const string ApiToken = "Cloudflare:ApiToken";
    }
}
