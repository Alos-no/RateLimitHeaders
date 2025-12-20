using System.Net;
using RateLimitHeaders.Parsing;
using RateLimitHeaders.Tests.Fixtures;

namespace RateLimitHeaders.Tests;

public class RateLimitHeaderParserTests
{
    [Fact]
    public void Parse_WithBothHeaders_ShouldReturnValidInfo()
    {
        // Arrange
        var response = MockHttpHandler.CreateRateLimitResponse(
            HttpStatusCode.OK,
            remaining: 50,
            resetSeconds: 30,
            quota: 100,
            windowSeconds: 60,
            policyName: "default");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("default");
        result.Remaining.Should().Be(50);
        result.ResetSeconds.Should().Be(30);
        result.Quota.Should().Be(100);
        result.WindowSeconds.Should().Be(60);
    }

    [Fact]
    public void Parse_WithOnlyRateLimitHeader_ShouldReturnPartialInfo()
    {
        // Arrange
        var response = MockHttpHandler.CreateRateLimitOnlyResponse(
            HttpStatusCode.OK,
            remaining: 25,
            resetSeconds: 15,
            policyName: "api-v1");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("api-v1");
        result.Remaining.Should().Be(25);
        result.ResetSeconds.Should().Be(15);
        result.Quota.Should().Be(0);  // Not provided
        result.WindowSeconds.Should().Be(0);  // Not provided
    }

    [Fact]
    public void Parse_WithNoHeaders_ShouldReturnInvalidInfo()
    {
        // Arrange
        var response = MockHttpHandler.CreateNoRateLimitResponse();

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Parse_WithMalformedRateLimitHeader_ShouldIgnoreSilently()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "malformed-header-value");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert - Per IETF spec, malformed headers should be silently ignored
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Parse_WithMalformedRateLimitPolicyHeader_ShouldIgnoreSilently()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"default\";r=50;t=30");
        response.Headers.Add("RateLimit-Policy", "malformed");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert - RateLimit header is valid, policy header ignored
        result.IsValid.Should().BeTrue();
        result.Remaining.Should().Be(50);
        result.Quota.Should().Be(0);  // Policy header was invalid
    }

    [Fact]
    public void Parse_WithQuotedPolicyName_ShouldExtractCorrectly()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"my-custom-policy\";r=100;t=60");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert
        result.PolicyName.Should().Be("my-custom-policy");
    }

    [Theory]
    [InlineData("\"default\";r=0;t=0", true, 0, 0)]
    [InlineData("\"default\";r=999999;t=3600", true, 999999, 3600)]
    [InlineData("\"default\"; r = 50 ; t = 30", true, 50, 30)]  // Whitespace tolerance
    [InlineData("\"default\";t=30;r=50", true, 50, 30)]  // Parameter order: t before r (RFC 8941 compliant)
    [InlineData("\"default\"; t = 30 ; r = 50", true, 50, 30)]  // Parameter order with whitespace
    public void Parse_RateLimitHeader_ShouldHandleVariousFormats(
        string headerValue,
        bool expectedValid,
        int expectedRemaining,
        int expectedReset)
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", headerValue);

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert
        result.IsValid.Should().Be(expectedValid);
        if (expectedValid)
        {
            result.Remaining.Should().Be(expectedRemaining);
            result.ResetSeconds.Should().Be(expectedReset);
        }
    }

    [Fact]
    public void Parse_WithRawStrings_ShouldParseCorrectly()
    {
        // Arrange
        var rateLimitHeader = "\"api\";r=75;t=45";
        var rateLimitPolicyHeader = "\"api\";q=100;w=60";

        // Act
        var result = RateLimitHeaderParser.Parse(rateLimitHeader, rateLimitPolicyHeader);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("api");
        result.Remaining.Should().Be(75);
        result.ResetSeconds.Should().Be(45);
        result.Quota.Should().Be(100);
        result.WindowSeconds.Should().Be(60);
    }

    [Fact]
    public void TryParse_WithValidHeaders_ShouldReturnTrue()
    {
        // Arrange
        var response = MockHttpHandler.CreateRateLimitResponse(
            HttpStatusCode.OK, 50, 30, 100, 60);

        // Act
        var success = RateLimitHeaderParser.TryParse(response, out var result);

        // Assert
        success.Should().BeTrue();
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void TryParse_WithInvalidHeaders_ShouldReturnFalse()
    {
        // Arrange
        var response = MockHttpHandler.CreateNoRateLimitResponse();

        // Act
        var success = RateLimitHeaderParser.TryParse(response, out var result);

        // Assert
        success.Should().BeFalse();
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Parse_WithNullResponse_ShouldThrowArgumentNullException()
    {
        // Arrange & Act & Assert
        var act = () => RateLimitHeaderParser.Parse((HttpResponseMessage)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Parse_WithDifferentPolicyNames_ShouldPreferRateLimitHeaderPolicy()
    {
        // Arrange - Different policy names in each header
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"rate-policy\";r=50;t=30");
        response.Headers.Add("RateLimit-Policy", "\"policy-policy\";q=100;w=60");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert - RateLimit header policy name takes precedence
        result.PolicyName.Should().Be("rate-policy");
    }

    [Theory]
    [InlineData("\"default\";r=-1;t=30")]  // Negative remaining
    [InlineData("\"default\";r=50;t=-1")]  // Negative reset
    [InlineData("\"default\";r=abc;t=30")]  // Non-numeric remaining
    [InlineData("\"default\";r=50;t=xyz")]  // Non-numeric reset
    [InlineData("default;r=50;t=30")]  // Missing quotes around policy
    [InlineData("\"default\"r=50;t=30")]  // Missing semicolon
    public void Parse_WithInvalidValues_ShouldReturnInvalid(string headerValue)
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", headerValue);

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert
        result.IsValid.Should().BeFalse();
    }

    #region Multiple Policies Tests

    [Fact]
    public void Parse_WithMultipleRateLimitPolicies_ShouldReturnMostRestrictive()
    {
        // Arrange - burst has lower remaining (50) than daily (900)
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"burst\";r=50;t=30,\"daily\";r=900;t=43200");
        response.Headers.Add("RateLimit-Policy", "\"burst\";q=100;w=60,\"daily\";q=1000;w=86400");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert - Should return burst policy (more restrictive: 50% vs 90% remaining)
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("burst");
        result.Remaining.Should().Be(50);
        result.ResetSeconds.Should().Be(30);
    }

    [Fact]
    public void Parse_WithMultiplePolicies_DailyMoreRestrictive_ShouldReturnDaily()
    {
        // Arrange - daily has lower remaining percentage (5%) than burst (80%)
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"burst\";r=80;t=30,\"daily\";r=50;t=43200");
        response.Headers.Add("RateLimit-Policy", "\"burst\";q=100;w=60,\"daily\";q=1000;w=86400");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert - Should return daily policy (5% remaining vs 80%)
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("daily");
        result.Remaining.Should().Be(50);
    }

    [Fact]
    public void Parse_WithMultiplePolicies_MatchesPolicyInfo()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"api\";r=10;t=30,\"user\";r=500;t=3600");
        response.Headers.Add("RateLimit-Policy", "\"api\";q=100;w=60,\"user\";q=1000;w=3600");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert - api is more restrictive (10% remaining)
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("api");
        result.Remaining.Should().Be(10);
        result.Quota.Should().Be(100);  // Matched from RateLimit-Policy
        result.WindowSeconds.Should().Be(60);
    }

    [Fact]
    public void Parse_WithMultipleRateLimitPoliciesOnly_ShouldReturnSmallestQuota()
    {
        // Arrange - Only RateLimit-Policy header with multiple policies
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit-Policy", "\"burst\";q=100;w=60,\"daily\";q=1000;w=86400");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert - Should return burst (smaller quota = more restrictive)
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("burst");
        result.Quota.Should().Be(100);
        result.WindowSeconds.Should().Be(60);
    }

    [Fact]
    public void Parse_WithSpacesAroundComma_ShouldParseBoth()
    {
        // Arrange - spaces around comma separator
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"burst\";r=50;t=30 , \"daily\";r=900;t=43200");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("burst");  // More restrictive
    }

    [Fact]
    public void Parse_WithOneMalformedPolicy_ShouldParseValidOne()
    {
        // Arrange - first policy is valid, second is malformed
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"valid\";r=50;t=30,malformed-entry");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert - Should parse the valid one
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("valid");
        result.Remaining.Should().Be(50);
    }

    [Fact]
    public void Parse_WithMultiplePolicies_ShouldMatchByPolicyName()
    {
        // Arrange
        var rateLimitHeader = "\"api\";r=25;t=45,\"user\";r=500;t=3600";
        var rateLimitPolicyHeader = "\"api\";q=100;w=60,\"user\";q=1000;w=3600";

        // Act
        var result = RateLimitHeaderParser.Parse(rateLimitHeader, rateLimitPolicyHeader);

        // Assert - api is most restrictive (25%)
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("api");
        result.Remaining.Should().Be(25);
        result.Quota.Should().Be(100);
        result.ResetSeconds.Should().Be(45);
        result.WindowSeconds.Should().Be(60);
    }

    [Fact]
    public void Parse_WithMismatchedPolicies_ShouldStillWork()
    {
        // Arrange - RateLimit has "burst" but RateLimit-Policy has "api"
        var rateLimitHeader = "\"burst\";r=10;t=30";
        var rateLimitPolicyHeader = "\"api\";q=100;w=60";

        // Act
        var result = RateLimitHeaderParser.Parse(rateLimitHeader, rateLimitPolicyHeader);

        // Assert - burst info without quota (no policy match)
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("burst");
        result.Remaining.Should().Be(10);
        result.Quota.Should().Be(0);  // No matching policy
    }

    [Fact]
    public void Parse_WithEmptyStringHeader_ShouldReturnInvalid()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("RateLimit", "");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Parse_WithMaxIntValues_ShouldParseCorrectly()
    {
        // Arrange - edge case with very large numbers
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"max\";r=2147483647;t=2147483647");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Remaining.Should().Be(int.MaxValue);
        result.ResetSeconds.Should().Be(int.MaxValue);
    }

    [Fact]
    public void Parse_WithOverflowValues_ShouldReturnInvalid()
    {
        // Arrange - values exceeding int.MaxValue
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"overflow\";r=9999999999999;t=30");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert - int.TryParse fails for overflow, so entry is skipped
        result.IsValid.Should().BeFalse();
    }

    #endregion

    #region Long Policy Names Tests

    [Fact]
    public void Parse_WithLongPolicyName_ShouldParseCorrectly()
    {
        // Arrange - very long policy name (256 characters)
        var longName = new string('a', 256);
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", $"\"{longName}\";r=50;t=30");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be(longName);
        result.Remaining.Should().Be(50);
    }

    [Fact]
    public void Parse_WithSpecialCharactersInPolicyName_ShouldParseCorrectly()
    {
        // Arrange - policy name with special characters (excluding quotes)
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"api-v2_rate.limit:user/endpoint\";r=50;t=30");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("api-v2_rate.limit:user/endpoint");
    }

    [Fact]
    public void Parse_WithUnicodePolicyName_ShouldParseCorrectly()
    {
        // Arrange - Unicode characters in policy name
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"ポリシー-政策\";r=50;t=30");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("ポリシー-政策");
    }

    [Fact]
    public void Parse_WithEmptyPolicyName_ShouldReturnInvalid()
    {
        // Arrange - empty quoted policy name
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"\";r=50;t=30");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert - regex requires at least one character in policy name
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Parse_WithWhitespaceOnlyPolicyName_ShouldParseCorrectly()
    {
        // Arrange - whitespace in policy name (valid per spec)
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"   \";r=50;t=30");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert - whitespace is allowed in policy names
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("   ");
    }

    #endregion

    #region Policy Name Matching Tests

    [Fact]
    public void Parse_WithCaseInsensitivePolicyNames_ShouldMatchCorrectly()
    {
        // Arrange - RateLimit uses "API" but RateLimit-Policy uses "api"
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"API\";r=50;t=30");
        response.Headers.Add("RateLimit-Policy", "\"api\";q=100;w=60");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert - Should match case-insensitively and get quota info
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("API");  // Uses RateLimit header's casing
        result.Remaining.Should().Be(50);
        result.Quota.Should().Be(100);  // Successfully matched with "api" policy
        result.WindowSeconds.Should().Be(60);
    }

    [Fact]
    public void Parse_WithMixedCasePolicyNames_ShouldMatchAllCorrectly()
    {
        // Arrange - Various case combinations
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"BuRsT\";r=20;t=30,\"DAILY\";r=800;t=43200");
        response.Headers.Add("RateLimit-Policy", "\"burst\";q=100;w=60,\"daily\";q=1000;w=86400");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert - Both should match case-insensitively
        // BuRsT: 20/100 = 20%, DAILY: 800/1000 = 80% -> BuRsT is more restrictive
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("BuRsT");
        result.Remaining.Should().Be(20);
        result.Quota.Should().Be(100);
    }

    #endregion

    #region Multiple Header Instances Tests

    [Fact]
    public void Parse_WithMultipleRateLimitHeaderInstances_ShouldCombine()
    {
        // Arrange - Same header sent twice (HTTP allows this)
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"burst\";r=50;t=30");
        response.Headers.Add("RateLimit", "\"daily\";r=900;t=43200");
        response.Headers.Add("RateLimit-Policy", "\"burst\";q=100;w=60,\"daily\";q=1000;w=86400");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert - Should parse both entries (burst: 50%, daily: 90% -> burst selected)
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("burst");
        result.Remaining.Should().Be(50);
    }

    #endregion

    #region Parameter Order Independence (RFC 8941 Compliance)

    [Fact]
    public void Parse_WithReversedParameterOrder_RateLimit_ShouldParseCorrectly()
    {
        // Arrange - t before r (RFC 8941 says parameters are order-independent)
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"default\";t=30;r=50");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("default");
        result.Remaining.Should().Be(50);
        result.ResetSeconds.Should().Be(30);
    }

    [Fact]
    public void Parse_WithReversedParameterOrder_RateLimitPolicy_ShouldParseCorrectly()
    {
        // Arrange - w before q (RFC 8941 says parameters are order-independent)
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"default\";r=50;t=30");
        response.Headers.Add("RateLimit-Policy", "\"default\";w=60;q=100");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("default");
        result.Remaining.Should().Be(50);
        result.ResetSeconds.Should().Be(30);
        result.Quota.Should().Be(100);
        result.WindowSeconds.Should().Be(60);
    }

    [Fact]
    public void Parse_WithReversedParameterOrder_BothHeaders_ShouldParseCorrectly()
    {
        // Arrange - Both headers with reversed parameter order
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"api\";t=45;r=75");
        response.Headers.Add("RateLimit-Policy", "\"api\";w=90;q=150;pk=partition1;qu=requests");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("api");
        result.Remaining.Should().Be(75);
        result.ResetSeconds.Should().Be(45);
        result.Quota.Should().Be(150);
        result.WindowSeconds.Should().Be(90);
        result.PartitionKey.Should().Be("partition1");
        result.QuotaUnit.Should().Be("requests");
    }

    [Fact]
    public void Parse_WithMixedParameterOrders_MultiplePolicies_ShouldParseCorrectly()
    {
        // Arrange - Multiple policies with different parameter orders
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"burst\";r=50;t=30,\"daily\";t=43200;r=900");
        response.Headers.Add("RateLimit-Policy", "\"burst\";q=100;w=60,\"daily\";w=86400;q=1000");

        // Act
        var result = RateLimitHeaderParser.Parse(response);

        // Assert - Should return burst (more restrictive: 50% vs 90%)
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("burst");
        result.Remaining.Should().Be(50);
        result.ResetSeconds.Should().Be(30);
        result.Quota.Should().Be(100);
        result.WindowSeconds.Should().Be(60);
    }

    #endregion

    #region Direct HttpResponseHeaders Overload Tests

    [Fact]
    public void Parse_WithHttpResponseHeaders_ShouldParseCorrectly()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"test\";r=75;t=45");
        response.Headers.Add("RateLimit-Policy", "\"test\";q=150;w=90");

        // Act - Use the HttpResponseHeaders overload directly
        var result = RateLimitHeaderParser.Parse(response.Headers);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("test");
        result.Remaining.Should().Be(75);
        result.Quota.Should().Be(150);
    }

    [Fact]
    public void TryParse_WithHttpResponseHeaders_ShouldReturnCorrectResult()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("RateLimit", "\"headers-test\";r=25;t=15");

        // Act - Use the HttpResponseHeaders overload directly
        var success = RateLimitHeaderParser.TryParse(response.Headers, out var result);

        // Assert
        success.Should().BeTrue();
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("headers-test");
        result.Remaining.Should().Be(25);
    }

    [Fact]
    public void Parse_WithNullHeaders_ShouldThrowArgumentNullException()
    {
        // Arrange & Act & Assert
        var act = () => RateLimitHeaderParser.Parse((System.Net.Http.Headers.HttpResponseHeaders)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void TryParse_WithNullHeaders_ShouldThrowArgumentNullException()
    {
        // Arrange & Act & Assert
        var act = () => RateLimitHeaderParser.TryParse((System.Net.Http.Headers.HttpResponseHeaders)null!, out _);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Raw String Edge Cases Tests

    [Fact]
    public void Parse_WithNullRawStrings_ShouldReturnInvalid()
    {
        // Act
        var result = RateLimitHeaderParser.Parse(null, null);

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Parse_WithWhitespaceOnlyRawStrings_ShouldReturnInvalid()
    {
        // Act
        var result = RateLimitHeaderParser.Parse("   ", "   ");

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Parse_WithOnlyRateLimitPolicyRawString_ShouldReturnValidInfo()
    {
        // Act - Only policy header, no RateLimit header
        var result = RateLimitHeaderParser.Parse(null, "\"api\";q=100;w=60");

        // Assert
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("api");
        result.Quota.Should().Be(100);
        result.WindowSeconds.Should().Be(60);
        result.Remaining.Should().Be(0);  // Not provided
        result.ResetSeconds.Should().Be(0);  // Not provided
    }

    #endregion

    #region Partition Key (pk) and Quota Unit (qu) Parameter Tests

    [Fact]
    public void Parse_WithPartitionKeyQuoted_ShouldExtractCorrectly()
    {
        // Arrange
        var rateLimitHeader = "\"api\";r=50;t=30";
        var rateLimitPolicyHeader = "\"api\";q=100;w=60;pk=\"tenant-123\"";

        // Act
        var result = RateLimitHeaderParser.Parse(rateLimitHeader, rateLimitPolicyHeader);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PartitionKey.Should().Be("tenant-123");
    }

    [Fact]
    public void Parse_WithPartitionKeyUnquoted_ShouldExtractCorrectly()
    {
        // Arrange
        var rateLimitHeader = "\"api\";r=50;t=30";
        var rateLimitPolicyHeader = "\"api\";q=100;w=60;pk=user-456";

        // Act
        var result = RateLimitHeaderParser.Parse(rateLimitHeader, rateLimitPolicyHeader);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PartitionKey.Should().Be("user-456");
    }

    [Fact]
    public void Parse_WithQuotaUnitQuoted_ShouldExtractCorrectly()
    {
        // Arrange
        var rateLimitHeader = "\"api\";r=50;t=30";
        var rateLimitPolicyHeader = "\"api\";q=100;w=60;qu=\"content-bytes\"";

        // Act
        var result = RateLimitHeaderParser.Parse(rateLimitHeader, rateLimitPolicyHeader);

        // Assert
        result.IsValid.Should().BeTrue();
        result.QuotaUnit.Should().Be("content-bytes");
    }

    [Fact]
    public void Parse_WithQuotaUnitUnquoted_ShouldExtractCorrectly()
    {
        // Arrange
        var rateLimitHeader = "\"api\";r=50;t=30";
        var rateLimitPolicyHeader = "\"api\";q=100;w=60;qu=requests";

        // Act
        var result = RateLimitHeaderParser.Parse(rateLimitHeader, rateLimitPolicyHeader);

        // Assert
        result.IsValid.Should().BeTrue();
        result.QuotaUnit.Should().Be("requests");
    }

    [Fact]
    public void Parse_WithBothPkAndQu_ShouldExtractBoth()
    {
        // Arrange
        var rateLimitHeader = "\"api\";r=50;t=30";
        var rateLimitPolicyHeader = "\"api\";q=100;w=60;pk=\"org-789\";qu=\"tokens\"";

        // Act
        var result = RateLimitHeaderParser.Parse(rateLimitHeader, rateLimitPolicyHeader);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PartitionKey.Should().Be("org-789");
        result.QuotaUnit.Should().Be("tokens");
    }

    [Fact]
    public void Parse_WithPkAndQuInReverseOrder_ShouldExtractBoth()
    {
        // Arrange - qu before pk
        var rateLimitHeader = "\"api\";r=50;t=30";
        var rateLimitPolicyHeader = "\"api\";q=100;w=60;qu=bytes;pk=key123";

        // Act
        var result = RateLimitHeaderParser.Parse(rateLimitHeader, rateLimitPolicyHeader);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PartitionKey.Should().Be("key123");
        result.QuotaUnit.Should().Be("bytes");
    }

    [Fact]
    public void Parse_WithNoPkOrQu_ShouldReturnNullForBoth()
    {
        // Arrange - standard format without optional params
        var rateLimitHeader = "\"api\";r=50;t=30";
        var rateLimitPolicyHeader = "\"api\";q=100;w=60";

        // Act
        var result = RateLimitHeaderParser.Parse(rateLimitHeader, rateLimitPolicyHeader);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PartitionKey.Should().BeNull();
        result.QuotaUnit.Should().BeNull();
    }

    [Fact]
    public void Parse_WithMultiplePoliciesAndDifferentPkQu_ShouldMatchCorrectly()
    {
        // Arrange - two policies with different pk/qu values
        var rateLimitHeader = "\"burst\";r=10;t=30,\"daily\";r=900;t=43200";
        var rateLimitPolicyHeader = "\"burst\";q=100;w=60;pk=\"burst-partition\";qu=requests,\"daily\";q=1000;w=86400;pk=\"daily-partition\";qu=bytes";

        // Act
        var result = RateLimitHeaderParser.Parse(rateLimitHeader, rateLimitPolicyHeader);

        // Assert - burst is more restrictive (10%), should get its pk/qu values
        result.IsValid.Should().BeTrue();
        result.PolicyName.Should().Be("burst");
        result.PartitionKey.Should().Be("burst-partition");
        result.QuotaUnit.Should().Be("requests");
    }

    [Fact]
    public void Parse_WithPkQuOnlyInPolicyHeader_ShouldBeAvailableWithPolicyOnlyParse()
    {
        // Arrange - Only RateLimit-Policy header with pk/qu
        var result = RateLimitHeaderParser.Parse(null, "\"api\";q=100;w=60;pk=\"tenant\";qu=\"requests\"");

        // Assert
        result.IsValid.Should().BeTrue();
        result.PartitionKey.Should().Be("tenant");
        result.QuotaUnit.Should().Be("requests");
    }

    [Fact]
    public void Parse_WithPkContainingSpecialChars_ShouldExtractCorrectly()
    {
        // Arrange - pk with hyphens, underscores, and other chars
        var rateLimitHeader = "\"api\";r=50;t=30";
        var rateLimitPolicyHeader = "\"api\";q=100;w=60;pk=\"tenant-org_123.prod\"";

        // Act
        var result = RateLimitHeaderParser.Parse(rateLimitHeader, rateLimitPolicyHeader);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PartitionKey.Should().Be("tenant-org_123.prod");
    }

    [Fact]
    public void Parse_WithQuAsTokens_ShouldExtractCorrectly()
    {
        // Arrange - AI/ML API style quota unit
        var rateLimitHeader = "\"api\";r=50;t=30";
        var rateLimitPolicyHeader = "\"api\";q=1000000;w=86400;qu=tokens";

        // Act
        var result = RateLimitHeaderParser.Parse(rateLimitHeader, rateLimitPolicyHeader);

        // Assert
        result.IsValid.Should().BeTrue();
        result.QuotaUnit.Should().Be("tokens");
        result.Quota.Should().Be(1000000);
    }

    [Fact]
    public void RateLimitInfo_ToString_WithPkAndQu_ShouldIncludeThem()
    {
        // Arrange
        var info = new RateLimitInfo
        {
            PolicyName = "api",
            Remaining = 50,
            Quota = 100,
            ResetSeconds = 30,
            WindowSeconds = 60,
            PartitionKey = "tenant-123",
            QuotaUnit = "requests",
            IsValid = true
        };

        // Act
        var result = info.ToString();

        // Assert
        result.Should().Contain("pk=tenant-123");
        result.Should().Contain("qu=requests");
    }

    [Fact]
    public void RateLimitInfo_ToString_WithoutPkAndQu_ShouldNotIncludeThem()
    {
        // Arrange
        var info = new RateLimitInfo
        {
            PolicyName = "api",
            Remaining = 50,
            Quota = 100,
            ResetSeconds = 30,
            WindowSeconds = 60,
            IsValid = true
        };

        // Act
        var result = info.ToString();

        // Assert
        result.Should().NotContain("pk=");
        result.Should().NotContain("qu=");
    }

    [Fact]
    public void Parse_WithSpacesAroundPkAndQu_ShouldParseTolerant()
    {
        // Arrange - whitespace around parameters
        var rateLimitHeader = "\"api\";r=50;t=30";
        var rateLimitPolicyHeader = "\"api\";q=100;w=60; pk = \"tenant\" ; qu = \"bytes\"";

        // Act
        var result = RateLimitHeaderParser.Parse(rateLimitHeader, rateLimitPolicyHeader);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PartitionKey.Should().Be("tenant");
        result.QuotaUnit.Should().Be("bytes");
    }

    [Theory]
    [InlineData("pk=simple", "simple")]
    [InlineData("pk=\"quoted\"", "quoted")]
    [InlineData("pk=\"with spaces\"", "with spaces")]
    [InlineData("pk=key-with-dashes", "key-with-dashes")]
    public void Parse_VariousPkFormats_ShouldExtractCorrectly(string pkParam, string expectedValue)
    {
        // Arrange
        var rateLimitHeader = "\"api\";r=50;t=30";
        var rateLimitPolicyHeader = $"\"api\";q=100;w=60;{pkParam}";

        // Act
        var result = RateLimitHeaderParser.Parse(rateLimitHeader, rateLimitPolicyHeader);

        // Assert
        result.IsValid.Should().BeTrue();
        result.PartitionKey.Should().Be(expectedValue);
    }

    [Theory]
    [InlineData("qu=requests", "requests")]
    [InlineData("qu=\"content-bytes\"", "content-bytes")]
    [InlineData("qu=tokens", "tokens")]
    [InlineData("qu=\"custom-unit\"", "custom-unit")]
    public void Parse_VariousQuFormats_ShouldExtractCorrectly(string quParam, string expectedValue)
    {
        // Arrange
        var rateLimitHeader = "\"api\";r=50;t=30";
        var rateLimitPolicyHeader = $"\"api\";q=100;w=60;{quParam}";

        // Act
        var result = RateLimitHeaderParser.Parse(rateLimitHeader, rateLimitPolicyHeader);

        // Assert
        result.IsValid.Should().BeTrue();
        result.QuotaUnit.Should().Be(expectedValue);
    }

    #endregion
}
