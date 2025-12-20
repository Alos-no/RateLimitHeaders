using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RateLimitHeaders.Events;
using RateLimitHeaders.Internal;
using RateLimitHeaders.Parsing;

namespace RateLimitHeaders.Tests;

/// <summary>
/// Unit tests for the CallbackHelper internal class.
/// </summary>
public class CallbackHelperTests
{
    #region SafeInvoke (Synchronous) Tests

    [Fact]
    public void SafeInvoke_WithNullCallback_ShouldNotThrow()
    {
        // Arrange
        Action<int>? callback = null;

        // Act & Assert - Should not throw
        var act = () => CallbackHelper.SafeInvoke(callback, 42, NullLogger.Instance, "TestCallback");
        act.Should().NotThrow();
    }

    [Fact]
    public void SafeInvoke_WithValidCallback_ShouldInvokeCallback()
    {
        // Arrange
        var wasCalled = false;
        int receivedValue = 0;
        Action<int> callback = value =>
        {
            wasCalled = true;
            receivedValue = value;
        };

        // Act
        CallbackHelper.SafeInvoke(callback, 42, NullLogger.Instance, "TestCallback");

        // Assert
        wasCalled.Should().BeTrue();
        receivedValue.Should().Be(42);
    }

    [Fact]
    public void SafeInvoke_WithThrowingCallback_ShouldSwallowException()
    {
        // Arrange
        Action<string> callback = _ => throw new InvalidOperationException("Test exception");

        // Act & Assert - Should not throw
        var act = () => CallbackHelper.SafeInvoke(callback, "test", NullLogger.Instance, "TestCallback");
        act.Should().NotThrow();
    }

    [Fact]
    public void SafeInvoke_WithThrowingCallback_ShouldLogError()
    {
        // Arrange
        var loggerMock = new TestLogger();
        Action<string> callback = _ => throw new InvalidOperationException("Test exception");

        // Act
        CallbackHelper.SafeInvoke(callback, "test", loggerMock, "MyTestCallback");

        // Assert
        loggerMock.LoggedMessages.Should().ContainSingle();
        loggerMock.LoggedMessages[0].LogLevel.Should().Be(LogLevel.Error);
        loggerMock.LoggedMessages[0].Message.Should().Contain("MyTestCallback");
    }

    #endregion

    #region SafeInvokeAsync (with Logger) Tests

    [Fact]
    public async Task SafeInvokeAsync_WithLogger_NullCallback_ShouldNotThrow()
    {
        // Arrange
        Func<int, ValueTask>? callback = null;

        // Act & Assert
        var act = async () => await CallbackHelper.SafeInvokeAsync(callback, 42, NullLogger.Instance, "TestCallback");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SafeInvokeAsync_WithLogger_ValidCallback_ShouldInvokeCallback()
    {
        // Arrange
        var wasCalled = false;
        int receivedValue = 0;
        Func<int, ValueTask> callback = value =>
        {
            wasCalled = true;
            receivedValue = value;
            return ValueTask.CompletedTask;
        };

        // Act
        await CallbackHelper.SafeInvokeAsync(callback, 42, NullLogger.Instance, "TestCallback");

        // Assert
        wasCalled.Should().BeTrue();
        receivedValue.Should().Be(42);
    }

    [Fact]
    public async Task SafeInvokeAsync_WithLogger_SyncThrowingCallback_ShouldSwallowException()
    {
        // Arrange - Exception thrown synchronously (before returning ValueTask)
        Func<string, ValueTask> callback = _ => throw new InvalidOperationException("Sync exception");

        // Act & Assert
        var act = async () => await CallbackHelper.SafeInvokeAsync(callback, "test", NullLogger.Instance, "TestCallback");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SafeInvokeAsync_WithLogger_AsyncThrowingCallback_ShouldSwallowException()
    {
        // Arrange - Exception thrown asynchronously
        Func<string, ValueTask> callback = async _ =>
        {
            await Task.Delay(1);
            throw new InvalidOperationException("Async exception");
        };

        // Act & Assert
        var act = async () => await CallbackHelper.SafeInvokeAsync(callback, "test", NullLogger.Instance, "TestCallback");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SafeInvokeAsync_WithLogger_ThrowingCallback_ShouldLogError()
    {
        // Arrange
        var loggerMock = new TestLogger();
        Func<string, ValueTask> callback = _ => throw new InvalidOperationException("Test exception");

        // Act
        await CallbackHelper.SafeInvokeAsync(callback, "test", loggerMock, "MyAsyncCallback");

        // Assert
        loggerMock.LoggedMessages.Should().ContainSingle();
        loggerMock.LoggedMessages[0].LogLevel.Should().Be(LogLevel.Error);
        loggerMock.LoggedMessages[0].Message.Should().Contain("MyAsyncCallback");
    }

    #endregion

    #region SafeInvokeAsync (without Logger) Tests

    [Fact]
    public async Task SafeInvokeAsync_NoLogger_NullCallback_ShouldNotThrow()
    {
        // Arrange
        Func<int, ValueTask>? callback = null;

        // Act & Assert
        var act = async () => await CallbackHelper.SafeInvokeAsync(callback, 42);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SafeInvokeAsync_NoLogger_ValidCallback_ShouldInvokeCallback()
    {
        // Arrange
        var wasCalled = false;
        Func<int, ValueTask> callback = _ =>
        {
            wasCalled = true;
            return ValueTask.CompletedTask;
        };

        // Act
        await CallbackHelper.SafeInvokeAsync(callback, 42);

        // Assert
        wasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task SafeInvokeAsync_NoLogger_ThrowingCallback_ShouldSwallowException()
    {
        // Arrange
        Func<string, ValueTask> callback = _ => throw new InvalidOperationException("Test exception");

        // Act & Assert - Exception should be swallowed silently
        var act = async () => await CallbackHelper.SafeInvokeAsync(callback, "test");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SafeInvokeAsync_NoLogger_AsyncThrowingCallback_ShouldSwallowException()
    {
        // Arrange
        Func<string, ValueTask> callback = async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("Async exception without logger");
        };

        // Act & Assert
        var act = async () => await CallbackHelper.SafeInvokeAsync(callback, "test");
        await act.Should().NotThrowAsync();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task SafeInvokeAsync_WithOperationCanceledException_ShouldSwallow()
    {
        // Arrange
        Func<int, ValueTask> callback = _ => throw new OperationCanceledException();

        // Act & Assert
        var act = async () => await CallbackHelper.SafeInvokeAsync(callback, 42, NullLogger.Instance, "TestCallback");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SafeInvokeAsync_WithAggregateException_ShouldSwallow()
    {
        // Arrange
        Func<int, ValueTask> callback = _ => throw new AggregateException(
            new InvalidOperationException("Inner 1"),
            new ArgumentException("Inner 2"));

        // Act & Assert
        var act = async () => await CallbackHelper.SafeInvokeAsync(callback, 42, NullLogger.Instance, "TestCallback");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void SafeInvoke_WithComplexArg_ShouldPassCorrectly()
    {
        // Arrange
        RateLimitEventArgs? receivedArgs = null;
        var expectedInfo = new RateLimitInfo
        {
            PolicyName = "test-policy",
            Remaining = 50,
            Quota = 100,
            IsValid = true
        };
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var expectedArgs = new RateLimitEventArgs(expectedInfo, new Uri("http://test.com"), response);

        Action<RateLimitEventArgs> callback = args => receivedArgs = args;

        // Act
        CallbackHelper.SafeInvoke(callback, expectedArgs, NullLogger.Instance, "TestCallback");

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Value.RateLimitInfo.PolicyName.Should().Be("test-policy");
        receivedArgs.Value.RateLimitInfo.Remaining.Should().Be(50);
    }

    #endregion

    #region Test Helper

    /// <summary>
    /// Simple logger implementation for testing that captures logged messages.
    /// </summary>
    private sealed class TestLogger : ILogger
    {
        public List<LogEntry> LoggedMessages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            LoggedMessages.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }

        public sealed record LogEntry(LogLevel LogLevel, string Message, Exception? Exception);
    }

    #endregion
}
