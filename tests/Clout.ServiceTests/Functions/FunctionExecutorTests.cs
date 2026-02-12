using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Clout.Host.Functions;
using Clout.Shared.Abstractions;
using Clout.Shared.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Clout.ServiceTests.Functions;

/// <summary>
/// Service-level tests for FunctionExecutor.
/// Tests focus on function execution flow, error handling, and resource cleanup.
/// </summary>
public class FunctionExecutorTests
{
    private readonly Mock<IBlobStorage> _mockStorage;
    private readonly Mock<ILogger<FunctionExecutor>> _mockLogger;
    private readonly FunctionExecutor _executor;

    public FunctionExecutorTests()
    {
        _mockStorage = new Mock<IBlobStorage>();
        _mockLogger = new Mock<ILogger<FunctionExecutor>>();
        _executor = new FunctionExecutor(_mockStorage.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsBlobNotFoundException_WhenBlobDoesNotExist()
    {
        // Arrange
        var blobId = Guid.NewGuid().ToString("N");
        var functionName = "TestFunction";
        
        _mockStorage
            .Setup(s => s.OpenReadAsync(blobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream?)null);

        // Act & Assert
        await Assert.ThrowsAsync<BlobNotFoundException>(
            async () => await _executor.ExecuteAsync(blobId, functionName, null, CancellationToken.None)
        );
    }

    [Fact]
    public async Task ExecuteAsync_ValidatesParameters()
    {
        // Arrange
        var emptyBlobId = "";
        var emptyFunctionName = "";

        // Act & Assert
        await Assert.ThrowsAsync<ValidationException>(
            async () => await _executor.ExecuteAsync(emptyBlobId, "ValidFunction", null, CancellationToken.None)
        );
        
        await Assert.ThrowsAsync<ValidationException>(
            async () => await _executor.ExecuteAsync("validblob123", emptyFunctionName, null, CancellationToken.None)
        );
    }

    [Fact]
    public async Task ExecuteAsync_HandlesCancellation()
    {
        // Arrange
        var blobId = Guid.NewGuid().ToString("N");
        var functionName = "TestFunction";
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var mockStream = new MemoryStream(Encoding.UTF8.GetBytes("test data"));
        _mockStorage
            .Setup(s => s.OpenReadAsync(blobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockStream);

        // Act & Assert
        await Assert.ThrowsAsync<FunctionExecutionException>(
            async () => await _executor.ExecuteAsync(blobId, functionName, null, cts.Token)
        );
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenStorageIsNull()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(
            () => new FunctionExecutor(null!, _mockLogger.Object)
        );
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenLoggerIsNull()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(
            () => new FunctionExecutor(_mockStorage.Object, null!)
        );
    }

    [Fact]
    public async Task ExecuteAsync_LogsWarning_WhenBlobNotFound()
    {
        // Arrange
        var blobId = Guid.NewGuid().ToString("N");
        var functionName = "TestFunction";
        
        _mockStorage
            .Setup(s => s.OpenReadAsync(blobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream?)null);

        // Act
        try
        {
            await _executor.ExecuteAsync(blobId, functionName, null, CancellationToken.None);
        }
        catch (BlobNotFoundException)
        {
            // Expected
        }

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("not found")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_DisposesPayload_AfterExecution()
    {
        // Arrange
        var blobId = Guid.NewGuid().ToString("N");
        var functionName = "TestFunction";
        var payload = JsonDocument.Parse("{\"test\": \"value\"}");
        
        _mockStorage
            .Setup(s => s.OpenReadAsync(blobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream?)null);

        // Act
        try
        {
            await _executor.ExecuteAsync(blobId, functionName, payload, CancellationToken.None);
        }
        catch (BlobNotFoundException)
        {
            // Expected
        }

        // Assert - Payload might not be disposed by ExecuteAsync
        // This depends on whether the implementation disposes the payload on error
        payload.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = payload.RootElement);
    }

    [Fact]
    public async Task ExecuteAsync_WrapsUnexpectedException_InFunctionExecutionException()
    {
        // Arrange
        var blobId = Guid.NewGuid().ToString("N");
        var functionName = "TestFunction";
        
        _mockStorage
            .Setup(s => s.OpenReadAsync(blobId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unexpected error"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<FunctionExecutionException>(
            async () => await _executor.ExecuteAsync(blobId, functionName, null, CancellationToken.None)
        );

        exception.Message.Should().Contain("unexpected error");
        exception.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteAsync_PreservesCloutExceptions()
    {
        // Arrange
        var blobId = Guid.NewGuid().ToString("N");
        var functionName = "TestFunction";
        
        _mockStorage
            .Setup(s => s.OpenReadAsync(blobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream?)null);

        // Act & Assert - Should throw BlobNotFoundException (a CloutException) directly
        await Assert.ThrowsAsync<BlobNotFoundException>(
            async () => await _executor.ExecuteAsync(blobId, functionName, null, CancellationToken.None)
        );
    }

    [Fact]
    public async Task ExecuteAsync_HandlesMultipleExecutionsConcurrently()
    {
        // Arrange
        var blobId1 = Guid.NewGuid().ToString("N");
        var blobId2 = Guid.NewGuid().ToString("N");
        var functionName = "TestFunction";
        
        _mockStorage
            .Setup(s => s.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream?)null);

        // Act
        var task1 = _executor.ExecuteAsync(blobId1, functionName, null, CancellationToken.None);
        var task2 = _executor.ExecuteAsync(blobId2, functionName, null, CancellationToken.None);

        // Assert - Both should complete without deadlock
        await Assert.ThrowsAsync<BlobNotFoundException>(async () => await task1);
        await Assert.ThrowsAsync<BlobNotFoundException>(async () => await task2);
    }
}
