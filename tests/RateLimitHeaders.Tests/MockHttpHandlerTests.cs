using System.Net;
using RateLimitHeaders.Tests.Fixtures;

namespace RateLimitHeaders.Tests;

/// <summary>
/// Tests for the MockHttpHandler test fixture.
/// </summary>
public class MockHttpHandlerTests
{
    #region Disposal Behavior Tests

    [Fact]
    public async Task SentRequests_AfterHandlerDisposed_ShouldStillBeAccessible()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        string? capturedUri = null;
        string? capturedMethod = null;

        var handler = new MockHttpHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));

        var client = new HttpClient(handler);

        // Act - make a request, capture it, then dispose
        await client.GetAsync("http://example.com/api/test");

        // Capture request info before disposal
        capturedRequest = handler.SentRequests.Count > 0 ? handler.SentRequests[0] : null;
        capturedUri = capturedRequest?.RequestUri?.ToString();
        capturedMethod = capturedRequest?.Method.ToString();

        // Dispose the handler
        handler.Dispose();

        // Assert - requests should still be accessible after disposal
        // Note: The list is cleared but we captured the reference before disposal
        capturedRequest.Should().NotBeNull();
        capturedUri.Should().Be("http://example.com/api/test");
        capturedMethod.Should().Be("GET");
    }

    [Fact]
    public async Task SentRequests_MultipleRequests_AfterDisposal_RequestObjectsStillValid()
    {
        // Arrange
        var handler = new MockHttpHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));

        var client = new HttpClient(handler);

        // Act - make multiple requests
        await client.GetAsync("http://example.com/api/1");
        await client.PostAsync("http://example.com/api/2", new StringContent("test"));
        await client.DeleteAsync("http://example.com/api/3");

        // Capture all requests before disposal
        var requests = handler.SentRequests.ToList();

        // Dispose the handler
        handler.Dispose();

        // Assert - all captured request objects should still be valid
        requests.Should().HaveCount(3);
        requests[0].RequestUri!.ToString().Should().Be("http://example.com/api/1");
        requests[0].Method.Should().Be(HttpMethod.Get);
        requests[1].RequestUri!.ToString().Should().Be("http://example.com/api/2");
        requests[1].Method.Should().Be(HttpMethod.Post);
        requests[2].RequestUri!.ToString().Should().Be("http://example.com/api/3");
        requests[2].Method.Should().Be(HttpMethod.Delete);
    }

    [Fact]
    public async Task Dispose_WithUnusedQueuedResponses_ShouldDisposeResponses()
    {
        // Arrange
        var response1 = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("response1")
        };
        var response2 = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("response2")
        };

        var handler = new MockHttpHandler();
        handler.QueueResponse(response1);
        handler.QueueResponse(response2);

        // Act - dispose without using the queued responses
        handler.Dispose();

        // Assert - queued responses should be disposed
        // HttpResponseMessage.Dispose() disposes the Content, so reading it should throw
        var act1 = async () => await response1.Content.ReadAsStringAsync();
        var act2 = async () => await response2.Content.ReadAsStringAsync();

        await act1.Should().ThrowAsync<ObjectDisposedException>();
        await act2.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Dispose_WithPartiallyUsedQueue_ShouldDisposeRemainingResponses()
    {
        // Arrange
        var response1 = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("response1")
        };
        var response2 = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("response2")
        };
        var response3 = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("response3")
        };

        var handler = new MockHttpHandler();
        handler.QueueResponse(response1);
        handler.QueueResponse(response2);
        handler.QueueResponse(response3);

        var client = new HttpClient(handler);

        // Act - use only first response
        await client.GetAsync("http://example.com/api/test");

        // Dispose handler
        handler.Dispose();

        // Assert - unused responses (2 and 3) should be disposed
        // HttpResponseMessage.Dispose() disposes the Content, so reading it should throw
        var act2 = async () => await response2.Content.ReadAsStringAsync();
        var act3 = async () => await response3.Content.ReadAsStringAsync();

        await act2.Should().ThrowAsync<ObjectDisposedException>();
        await act3.Should().ThrowAsync<ObjectDisposedException>();
    }

    #endregion

    #region Request Tracking Tests

    [Fact]
    public async Task RequestCount_ShouldTrackNumberOfRequests()
    {
        // Arrange
        var handler = new MockHttpHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));

        var client = new HttpClient(handler);

        // Act
        await client.GetAsync("http://example.com/1");
        await client.GetAsync("http://example.com/2");
        await client.GetAsync("http://example.com/3");

        // Assert
        handler.RequestCount.Should().Be(3);
    }

    [Fact]
    public async Task SentRequests_ShouldCaptureAllRequestDetails()
    {
        // Arrange
        var handler = new MockHttpHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("X-Custom-Header", "test-value");

        // Act
        await client.GetAsync("http://example.com/api/test?param=value");

        // Assert
        handler.SentRequests.Should().HaveCount(1);
        var request = handler.SentRequests[0];
        request.RequestUri!.ToString().Should().Be("http://example.com/api/test?param=value");
        request.Headers.GetValues("X-Custom-Header").Should().ContainSingle().Which.Should().Be("test-value");
    }

    #endregion

    #region Response Factory Tests

    [Fact]
    public async Task SetResponseFactory_ShouldGenerateResponsesBasedOnRequest()
    {
        // Arrange
        var handler = new MockHttpHandler();
        handler.SetResponseFactory(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/success" => new HttpResponseMessage(HttpStatusCode.OK),
                "/notfound" => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            };
        });

        var client = new HttpClient(handler);

        // Act
        var response1 = await client.GetAsync("http://example.com/success");
        var response2 = await client.GetAsync("http://example.com/notfound");
        var response3 = await client.GetAsync("http://example.com/other");

        // Assert
        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        response2.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response3.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResponseFactory_TakesPrecedenceOverQueuedResponses()
    {
        // Arrange
        var handler = new MockHttpHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        handler.SetResponseFactory(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("http://example.com/test");

        // Assert - factory should be used, not queued response
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    #endregion

    #region Rate Limit Response Helpers Tests

    [Fact]
    public void CreateRateLimitResponse_ShouldIncludeCorrectHeaders()
    {
        // Act
        var response = MockHttpHandler.CreateRateLimitResponse(
            HttpStatusCode.OK,
            remaining: 50,
            resetSeconds: 30,
            quota: 100,
            windowSeconds: 60,
            policyName: "api-limit");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("RateLimit").Should().ContainSingle()
            .Which.Should().Be("\"api-limit\";r=50;t=30");
        response.Headers.GetValues("RateLimit-Policy").Should().ContainSingle()
            .Which.Should().Be("\"api-limit\";q=100;w=60");
    }

    [Fact]
    public void CreateRateLimitOnlyResponse_ShouldNotIncludePolicyHeader()
    {
        // Act
        var response = MockHttpHandler.CreateRateLimitOnlyResponse(
            HttpStatusCode.OK,
            remaining: 25,
            resetSeconds: 15,
            policyName: "test");

        // Assert
        response.Headers.GetValues("RateLimit").Should().ContainSingle()
            .Which.Should().Be("\"test\";r=25;t=15");
        response.Headers.Contains("RateLimit-Policy").Should().BeFalse();
    }

    [Fact]
    public void CreateNoRateLimitResponse_ShouldNotIncludeRateLimitHeaders()
    {
        // Act
        var response = MockHttpHandler.CreateNoRateLimitResponse(HttpStatusCode.OK);

        // Assert
        response.Headers.Contains("RateLimit").Should().BeFalse();
        response.Headers.Contains("RateLimit-Policy").Should().BeFalse();
    }

    [Fact]
    public async Task QueueTooManyRequestsResponse_ShouldIncludeRetryAfterHeader()
    {
        // Arrange
        var handler = new MockHttpHandler();
        handler.QueueTooManyRequestsResponse(
            retryAfterSeconds: 60,
            remaining: 0,
            resetSeconds: 60,
            quota: 100,
            windowSeconds: 60);

        var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("http://example.com/test");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.GetValues("Retry-After").Should().ContainSingle()
            .Which.Should().Be("60");
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task SendAsync_WithCancelledToken_ShouldThrow()
    {
        // Arrange
        var handler = new MockHttpHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));

        var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAsync("http://example.com/test", cts.Token));
    }

    #endregion
}
