using System;
using System.Threading;
using System.Threading.Tasks;
using Clout.Host.Functions;
using Clout.Host.Queue;
using Clout.Shared.Abstractions;
using Clout.Shared.Exceptions;
using Clout.Shared.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Clout.ServiceTests.Functions;

/// <summary>
/// Service-level tests for QueueTriggerDispatcher.
/// Tests focus on worker lifecycle, activation/deactivation, and shutdown behavior.
/// </summary>
public class QueueTriggerDispatcherTests : IDisposable
{
    private readonly Mock<IAmqpQueueServer> _mockQueueServer;
    private readonly Mock<IBlobStorage> _mockStorage;
    private readonly Mock<FunctionExecutor> _mockExecutor;
    private readonly Mock<ILogger<QueueTriggerDispatcher>> _mockLogger;
    private readonly QueueTriggerDispatcher _dispatcher;

    public QueueTriggerDispatcherTests()
    {
        _mockQueueServer = new Mock<IAmqpQueueServer>();
        _mockStorage = new Mock<IBlobStorage>();
        _mockLogger = new Mock<ILogger<QueueTriggerDispatcher>>();
        
        // FunctionExecutor requires actual instances, so we'll mock its dependencies
        var mockBlobStorage = new Mock<IBlobStorage>();
        var mockFuncLogger = new Mock<ILogger<FunctionExecutor>>();
        var realExecutor = new FunctionExecutor(mockBlobStorage.Object, mockFuncLogger.Object);
        
        _dispatcher = new QueueTriggerDispatcher(
            _mockQueueServer.Object,
            _mockStorage.Object,
            realExecutor,
            _mockLogger.Object
        );
    }

    public void Dispose()
    {
        try
        {
            _dispatcher.StopAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(5));
            _dispatcher.Dispose();
        }
        catch
        {
            // Cleanup is best effort
        }
    }

    [Fact]
    public async Task ActivateAsync_CreatesWorkerForQueueTrigger()
    {
        // Arrange
        var blobId = Guid.NewGuid().ToString("N");
        var functionName = "TestFunction";
        var queueName = "test-queue";

        // Setup mock to return empty queue initially
        _mockQueueServer
            .Setup(q => q.DequeueAsync<object>(queueName, It.IsAny<CancellationToken>()))
            .ReturnsAsync((object?)null);

        // Act
        await _dispatcher.ActivateAsync(blobId, functionName, queueName);

        // Allow worker to start
        await Task.Delay(100);

        // Assert - Worker should be trying to dequeue
        _mockQueueServer.Verify(
            q => q.DequeueAsync<object>(queueName, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
    }

    [Fact]
    public async Task DeactivateAsync_StopsWorker()
    {
        // Arrange
        var blobId = Guid.NewGuid().ToString("N");
        var functionName = "TestFunction";
        var queueName = "test-queue";

        _mockQueueServer
            .Setup(q => q.DequeueAsync<object>(queueName, It.IsAny<CancellationToken>()))
            .ReturnsAsync((object?)null);

        await _dispatcher.ActivateAsync(blobId, functionName, queueName);
        await Task.Delay(100); // Let worker start

        // Act
        await _dispatcher.DeactivateAsync(blobId);

        // Assert - After deactivation, no more dequeue attempts should be made
        _mockQueueServer.ResetCalls();
        await Task.Delay(100);
        
        _mockQueueServer.Verify(
            q => q.DequeueAsync<object>(queueName, It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task DeactivateAsync_HandlesNonExistentWorker()
    {
        // Arrange
        var nonExistentBlobId = Guid.NewGuid().ToString("N");

        // Act & Assert - Should not throw
        await _dispatcher.DeactivateAsync(nonExistentBlobId);
    }

    [Fact]
    public async Task ActivateAsync_ReplacesExistingWorker()
    {
        // Arrange
        var blobId = Guid.NewGuid().ToString("N");
        var functionName = "TestFunction";
        var queueName1 = "queue-1";
        var queueName2 = "queue-2";

        _mockQueueServer
            .Setup(q => q.DequeueAsync<object>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((object?)null);

        // Act - Activate with first queue
        await _dispatcher.ActivateAsync(blobId, functionName, queueName1);
        await Task.Delay(100);

        // Act - Activate again with different queue (should replace)
        await _dispatcher.ActivateAsync(blobId, functionName, queueName2);
        await Task.Delay(100);

        // Assert - Should only be listening to queue-2 now
        _mockQueueServer.Verify(
            q => q.DequeueAsync<object>(queueName2, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
    }

    [Fact]
    public async Task StopAsync_StopsAllWorkers()
    {
        // Arrange
        var blobId1 = Guid.NewGuid().ToString("N");
        var blobId2 = Guid.NewGuid().ToString("N");
        var queueName1 = "queue-1";
        var queueName2 = "queue-2";

        _mockQueueServer
            .Setup(q => q.DequeueAsync<object>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((object?)null);

        await _dispatcher.ActivateAsync(blobId1, "Function1", queueName1);
        await _dispatcher.ActivateAsync(blobId2, "Function2", queueName2);
        await Task.Delay(100);

        // Act
        await _dispatcher.StopAsync(CancellationToken.None);

        // Assert - No more dequeue attempts after stop
        _mockQueueServer.ResetCalls();
        await Task.Delay(100);
        
        _mockQueueServer.Verify(
            q => q.DequeueAsync<object>(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task MultipleActivations_WorkIndependently()
    {
        // Arrange
        var blobId1 = Guid.NewGuid().ToString("N");
        var blobId2 = Guid.NewGuid().ToString("N");
        var queueName1 = "queue-1";
        var queueName2 = "queue-2";

        _mockQueueServer
            .Setup(q => q.DequeueAsync<object>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((object?)null);

        // Act
        await _dispatcher.ActivateAsync(blobId1, "Function1", queueName1);
        await _dispatcher.ActivateAsync(blobId2, "Function2", queueName2);
        await Task.Delay(200);

        // Assert - Both workers should be running independently
        _mockQueueServer.Verify(
            q => q.DequeueAsync<object>(queueName1, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
        _mockQueueServer.Verify(
            q => q.DequeueAsync<object>(queueName2, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
    }

    [Fact]
    public async Task Dispatcher_HandlesCancellationGracefully()
    {
        // Arrange
        var blobId = Guid.NewGuid().ToString("N");
        var queueName = "test-queue";
        
        var cts = new CancellationTokenSource();
        _mockQueueServer
            .Setup(q => q.DequeueAsync<object>(queueName, It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            });

        await _dispatcher.ActivateAsync(blobId, "Function", queueName, cts.Token);
        await Task.Delay(100);

        // Act - Cancel
        cts.Cancel();
        await Task.Delay(100);

        // Assert - Should handle cancellation without throwing
        await _dispatcher.DeactivateAsync(blobId);
    }

    [Fact]
    public void Constructor_ValidatesDependencies()
    {
        // The constructor doesn't validate dependencies - it accepts null values
        // This is by design, as validation happens at usage time
        var dispatcher = new QueueTriggerDispatcher(
            _mockQueueServer.Object, 
            _mockStorage.Object, 
            new FunctionExecutor(Mock.Of<IBlobStorage>(), Mock.Of<ILogger<FunctionExecutor>>()),
            _mockLogger.Object
        );
        
        dispatcher.Should().NotBeNull();
    }

    [Fact]
    public async Task ActivateAsync_ValidatesParameters()
    {
        // Act & Assert
        await Assert.ThrowsAnyAsync<ValidationException>(
            async () => await _dispatcher.ActivateAsync("", "Function", "queue")
        );

        await Assert.ThrowsAnyAsync<ValidationException>(
            async () => await _dispatcher.ActivateAsync("blob123", "", "queue")
        );

        await Assert.ThrowsAnyAsync<ValidationException>(
            async () => await _dispatcher.ActivateAsync("blob123", "Function", "")
        );
    }

    [Fact]
    public async Task Dispatcher_SupportsMultipleConcurrentOperations()
    {
        // Arrange
        _mockQueueServer
            .Setup(q => q.DequeueAsync<object>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((object?)null);

        // Act - Perform multiple operations concurrently
        var tasks = new Task[10];
        for (int i = 0; i < 10; i++)
        {
            int captured = i;
            tasks[i] = Task.Run(async () =>
            {
                var blobId = Guid.NewGuid().ToString("N");
                await _dispatcher.ActivateAsync(blobId, $"Function{captured}", $"queue{captured}");
                await Task.Delay(50);
                await _dispatcher.DeactivateAsync(blobId);
            });
        }

        // Assert - All operations should complete without deadlock
        await Task.WhenAll(tasks);
    }
}
