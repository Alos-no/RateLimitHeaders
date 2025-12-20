using System.Globalization;
using System.Net;

namespace RateLimitHeaders.Tests.Fixtures;

/// <summary>
/// A mock HTTP handler for testing that allows configuring responses with rate limit headers.
/// </summary>
public sealed class MockHttpHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly List<HttpRequestMessage> _sentRequests = new();
    private Func<HttpRequestMessage, HttpResponseMessage>? _responseFactory;

    /// <summary>
    /// Gets all requests that were sent through this handler.
    /// </summary>
    public IReadOnlyList<HttpRequestMessage> SentRequests => _sentRequests;

    /// <summary>
    /// Gets the number of requests that were sent through this handler.
    /// </summary>
    public int RequestCount => _sentRequests.Count;

    /// <summary>
    /// Queues a response to be returned for the next request.
    /// </summary>
    /// <param name="response">The response to queue.</param>
    public void QueueResponse(HttpResponseMessage response)
    {
        _responses.Enqueue(response);
    }

    /// <summary>
    /// Queues a simple OK response with the specified rate limit headers.
    /// </summary>
    /// <param name="remaining">The remaining requests.</param>
    /// <param name="resetSeconds">The seconds until reset.</param>
    /// <param name="quota">The total quota.</param>
    /// <param name="windowSeconds">The window in seconds.</param>
    /// <param name="policyName">The policy name.</param>
    public void QueueRateLimitResponse(
        int remaining,
        int resetSeconds,
        int quota,
        int windowSeconds,
        string policyName = "default")
    {
        var response = CreateRateLimitResponse(
            HttpStatusCode.OK,
            remaining,
            resetSeconds,
            quota,
            windowSeconds,
            policyName);

        QueueResponse(response);
    }

    /// <summary>
    /// Queues a 429 Too Many Requests response with the specified headers.
    /// </summary>
    /// <param name="retryAfterSeconds">The Retry-After value in seconds.</param>
    /// <param name="remaining">The remaining requests (typically 0).</param>
    /// <param name="resetSeconds">The seconds until reset.</param>
    /// <param name="quota">The total quota.</param>
    /// <param name="windowSeconds">The window in seconds.</param>
    /// <param name="policyName">The policy name.</param>
    public void QueueTooManyRequestsResponse(
        int retryAfterSeconds,
        int remaining = 0,
        int resetSeconds = 60,
        int quota = 100,
        int windowSeconds = 60,
        string policyName = "default")
    {
        var response = CreateRateLimitResponse(
            HttpStatusCode.TooManyRequests,
            remaining,
            resetSeconds,
            quota,
            windowSeconds,
            policyName);

        response.Headers.Add("Retry-After", retryAfterSeconds.ToString(CultureInfo.InvariantCulture));

        QueueResponse(response);
    }

    /// <summary>
    /// Sets a factory function to generate responses for each request.
    /// </summary>
    /// <param name="factory">The factory function.</param>
    public void SetResponseFactory(Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        _responseFactory = factory;
    }

    /// <summary>
    /// Creates a response with the specified rate limit headers.
    /// </summary>
    public static HttpResponseMessage CreateRateLimitResponse(
        HttpStatusCode statusCode,
        int remaining,
        int resetSeconds,
        int quota,
        int windowSeconds,
        string policyName = "default")
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{\"ok\":true}")
        };

        // Add IETF RateLimit headers
        // RateLimit: "policy";r=remaining;t=reset_seconds
        response.Headers.Add("RateLimit", $"\"{policyName}\";r={remaining};t={resetSeconds}");

        // RateLimit-Policy: "policy";q=quota;w=window_seconds
        response.Headers.Add("RateLimit-Policy", $"\"{policyName}\";q={quota};w={windowSeconds}");

        return response;
    }

    /// <summary>
    /// Creates a response with only the RateLimit header (no RateLimit-Policy).
    /// </summary>
    public static HttpResponseMessage CreateRateLimitOnlyResponse(
        HttpStatusCode statusCode,
        int remaining,
        int resetSeconds,
        string policyName = "default")
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{\"ok\":true}")
        };

        response.Headers.Add("RateLimit", $"\"{policyName}\";r={remaining};t={resetSeconds}");

        return response;
    }

    /// <summary>
    /// Creates a response with no rate limit headers.
    /// </summary>
    public static HttpResponseMessage CreateNoRateLimitResponse(HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{\"ok\":true}")
        };
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _sentRequests.Add(request);

        HttpResponseMessage response;

        if (_responseFactory is not null)
        {
            response = _responseFactory(request);
        }
        else if (_responses.TryDequeue(out var queuedResponse))
        {
            response = queuedResponse;
        }
        else
        {
            // Default response if no responses queued
            response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}")
            };
        }

        // Ensure the response has the request message set (required for state tracking)
        response.RequestMessage = request;

        return Task.FromResult(response);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Note: We intentionally do NOT dispose requests in <see cref="SentRequests"/> because
    /// test code may still need to examine them after the handler is disposed.
    /// The requests will be garbage collected when the test completes.
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Clear the list but don't dispose requests - tests may still be examining them
            _sentRequests.Clear();

            // Dispose queued responses that were never used
            while (_responses.TryDequeue(out var response))
            {
                response.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
