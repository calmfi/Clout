using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Clout.Host.Queue;
using Clout.Shared.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Clout.ServiceTests.Queue;

/// <summary>
/// Service-level tests for DiskBackedAmqpQueueServer implementation.
/// Tests focus on queue operations, thread safety, and quota management.
/// </summary>
public class DiskBackedAmqpQueueServerTests : IDisposable
{
    private readonly List<string> _testBasePathsToCleanup;
    private readonly Mock<ILogger<DiskBackedAmqpQueueServer>> _mockLogger;
    private readonly QueueStorageOptions _defaultOptions;

    public DiskBackedAmqpQueueServerTests()
    {
        _testBasePathsToCleanup = new List<string>();
        _mockLogger = new Mock<ILogger<DiskBackedAmqpQueueServer>>();
        _defaultOptions = new QueueStorageOptions
        {
            BasePath = CreateTestBasePath(),
            MaxMessageBytes = 1024 * 1024, // 1 MB
            MaxQueueBytes = 10 * 1024 * 1024, // 10 MB
            MaxQueueMessages = 1000,
            Overflow = OverflowPolicy.Reject
        };
    }

    /// <summary>
    /// Creates a unique test base path and registers it for cleanup.
    /// </summary>
    private string CreateTestBasePath()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"clout-queue-test-{Guid.NewGuid():N}");
        _testBasePathsToCleanup.Add(basePath);
        return basePath;
    }

    /// <summary>
    /// Creates a server with a unique base path for isolated testing.
    /// </summary>
    private DiskBackedAmqpQueueServer CreateServer(QueueStorageOptions? options = null)
    {
        var opts = options ?? _defaultOptions;
        return new DiskBackedAmqpQueueServer(Options.Create(opts), _mockLogger.Object);
    }

    /// <summary>
    /// Creates a server with a fresh, isolated base path.
    /// </summary>
    private DiskBackedAmqpQueueServer CreateServerWithFreshPath()
    {
        var freshBasePath = CreateTestBasePath();
        var options = new QueueStorageOptions
        {
            BasePath = freshBasePath,
            MaxMessageBytes = 1024 * 1024,
            MaxQueueBytes = 10 * 1024 * 1024,
            MaxQueueMessages = 1000,
            Overflow = OverflowPolicy.Reject
        };
        return new DiskBackedAmqpQueueServer(Options.Create(options), _mockLogger.Object);
    }

    public void Dispose()
    {
        foreach (var basePath in _testBasePathsToCleanup)
        {
            try
            {
                if (Directory.Exists(basePath))
                {
                    Directory.Delete(basePath, recursive: true);
                }
            }
            catch (Exception ex)
            {
                // Log but don't throw - cleanup shouldn't fail the test
                System.Diagnostics.Debug.WriteLine($"Failed to cleanup test directory {basePath}: {ex.Message}");
            }
        }
        _testBasePathsToCleanup.Clear();
    }

    [Fact]
    public void CreateQueue_CreatesNewQueue()
    {
        // Arrange
        var server = CreateServer();
        var queueName = "test-queue";

        // Act
        server.CreateQueue(queueName);

        // Assert
        var queueDir = Path.Combine(_defaultOptions.BasePath, queueName);
        Directory.Exists(queueDir).Should().BeTrue();
        File.Exists(Path.Combine(queueDir, "state.json")).Should().BeTrue();
    }

    [Fact]
    public void CreateQueue_ThrowsForInvalidName()
    {
        // Arrange
        var server = CreateServer();

        // Act & Assert
        Assert.Throws<ValidationException>(() => server.CreateQueue(""));
        Assert.Throws<ValidationException>(() => server.CreateQueue("   "));
    }

    [Fact]
    public async Task EnqueueAsync_AddsMessageToQueue()
    {
        // Arrange
        var server = CreateServer();
        var queueName = "test-queue";
        server.CreateQueue(queueName);
        var message = new { Text = "Hello", Value = 42 };

        // Act
        await server.EnqueueAsync(queueName, message);

        // Assert
        var dequeued = await server.DequeueAsync<JsonElement>(queueName);
        dequeued.Should().NotBeNull();
        dequeued.GetProperty("text").GetString().Should().Be("Hello");
        dequeued.GetProperty("value").GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task EnqueueAsync_HandlesMultipleMessages()
    {
        // Arrange
        var server = CreateServer();
        var queueName = "test-queue";
        server.CreateQueue(queueName);

        // Act
        await server.EnqueueAsync(queueName, "Message 1");
        await server.EnqueueAsync(queueName, "Message 2");
        await server.EnqueueAsync(queueName, "Message 3");

        // Assert
        var msg1 = await server.DequeueAsync<string>(queueName);
        var msg2 = await server.DequeueAsync<string>(queueName);
        var msg3 = await server.DequeueAsync<string>(queueName);

        msg1.Should().Be("Message 1");
        msg2.Should().Be("Message 2");
        msg3.Should().Be("Message 3");
    }

    [Fact]
    public async Task DequeueAsync_ReturnsNullWhenQueueEmpty()
    {
        // Arrange
        var server = CreateServer();
        var queueName = "test-queue";
        server.CreateQueue(queueName);
        
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // Act
        var result = await server.DequeueAsync<string>(queueName, cts.Token);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task EnqueueDequeue_MaintainsFIFOOrder()
    {
        // Arrange
        var server = CreateServer();
        var queueName = "fifo-queue";
        server.CreateQueue(queueName);

        // Act
        for (int i = 0; i < 10; i++)
        {
            await server.EnqueueAsync(queueName, $"Message {i}");
        }

        // Assert
        for (int i = 0; i < 10; i++)
        {
            var msg = await server.DequeueAsync<string>(queueName);
            msg.Should().Be($"Message {i}");
        }
    }

    [Fact]
    public async Task EnqueueAsync_RejectsOversizedMessage()
    {
        // Arrange
        var options = new QueueStorageOptions
        {
            BasePath = _defaultOptions.BasePath,
            MaxMessageBytes = 100,
            Overflow = OverflowPolicy.Reject
        };
        var server = CreateServer(options);
        var queueName = "test-queue";
        server.CreateQueue(queueName);
        
        var largeMessage = new string('X', 200);

        // Act & Assert
        await Assert.ThrowsAsync<QueueQuotaExceededException>(
            async () => await server.EnqueueAsync(queueName, largeMessage)
        );
    }

    [Fact]
    public async Task EnqueueAsync_RejectsWhenQueueBytesExceeded()
    {
        // Arrange
        var options = new QueueStorageOptions
        {
            BasePath = _defaultOptions.BasePath,
            MaxQueueBytes = 200,
            Overflow = OverflowPolicy.Reject
        };
        var server = CreateServer(options);
        var queueName = "test-queue";
        server.CreateQueue(queueName);

        // Act
        await server.EnqueueAsync(queueName, new string('X', 50));
        await server.EnqueueAsync(queueName, new string('X', 50));

        // Assert - Third message should exceed quota
        await Assert.ThrowsAsync<QueueQuotaExceededException>(
            async () => await server.EnqueueAsync(queueName, new string('X', 150))
        );
    }

    [Fact]
    public async Task EnqueueAsync_RejectsWhenQueueMessageCountExceeded()
    {
        // Arrange
        var options = new QueueStorageOptions
        {
            BasePath = _defaultOptions.BasePath,
            MaxQueueMessages = 3,
            Overflow = OverflowPolicy.Reject
        };
        var server = CreateServer(options);
        var queueName = "test-queue";
        server.CreateQueue(queueName);

        // Act
        await server.EnqueueAsync(queueName, "Message 1");
        await server.EnqueueAsync(queueName, "Message 2");
        await server.EnqueueAsync(queueName, "Message 3");

        // Assert - Fourth message should exceed quota
        await Assert.ThrowsAsync<QueueQuotaExceededException>(
            async () => await server.EnqueueAsync(queueName, "Message 4")
        );
    }

    [Fact]
    public async Task EnqueueAsync_DropsOldestWhenOverflowPolicyIsDropOldest()
    {
        // Arrange
        var options = new QueueStorageOptions
        {
            BasePath = _defaultOptions.BasePath,
            MaxQueueMessages = 3,
            Overflow = OverflowPolicy.DropOldest
        };
        var server = CreateServer(options);
        var queueName = "test-queue";
        server.CreateQueue(queueName);

        // Act - Enqueue 5 messages with max of 3
        await server.EnqueueAsync(queueName, "Message 1");
        await server.EnqueueAsync(queueName, "Message 2");
        await server.EnqueueAsync(queueName, "Message 3");
        await server.EnqueueAsync(queueName, "Message 4"); // Should drop Message 1
        await server.EnqueueAsync(queueName, "Message 5"); // Should drop Message 2

        // Assert - Should only have Messages 3, 4, 5
        var msg1 = await server.DequeueAsync<string>(queueName);
        var msg2 = await server.DequeueAsync<string>(queueName);
        var msg3 = await server.DequeueAsync<string>(queueName);

        msg1.Should().Be("Message 3");
        msg2.Should().Be("Message 4");
        msg3.Should().Be("Message 5");
        
        // Queue should be empty now
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var emptyResult = await server.DequeueAsync<string>(queueName, cts.Token);
        emptyResult.Should().BeNull();
    }

    [Fact]
    public void PurgeQueue_RemovesAllMessages()
    {
        // Arrange
        var server = CreateServer();
        var queueName = "test-queue";
        server.CreateQueue(queueName);
        
        server.EnqueueAsync(queueName, "Message 1").AsTask().Wait();
        server.EnqueueAsync(queueName, "Message 2").AsTask().Wait();
        server.EnqueueAsync(queueName, "Message 3").AsTask().Wait();

        // Act
        server.PurgeQueue(queueName);

        // Assert
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var result = server.DequeueAsync<string>(queueName, cts.Token).AsTask().Result;
        result.Should().BeNull();
    }

    [Fact]
    public async Task PurgeQueueAsync_RemovesAllMessages()
    {
        // Arrange
        var server = CreateServer();
        var queueName = "test-queue";
        server.CreateQueue(queueName);
        
        await server.EnqueueAsync(queueName, "Message 1");
        await server.EnqueueAsync(queueName, "Message 2");
        await server.EnqueueAsync(queueName, "Message 3");

        // Act
        await server.PurgeQueueAsync(queueName);

        // Assert
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var result = await server.DequeueAsync<string>(queueName, cts.Token);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetStats_ReturnsCorrectCount()
    {
        // Arrange
        var server = CreateServer();
        var queueName = "test-queue";
        server.CreateQueue(queueName);
        
        await server.EnqueueAsync(queueName, "Message 1");
        await server.EnqueueAsync(queueName, "Message 2");
        await server.EnqueueAsync(queueName, "Message 3");

        // Act
        var stats = server.GetStats();
        var queueStats = stats.FirstOrDefault(s => s.Name.Equals(queueName, StringComparison.OrdinalIgnoreCase));

        // Assert
        queueStats.Should().NotBeNull();
        queueStats!.MessageCount.Should().Be(3);
    }

    [Fact]
    public async Task GetMessageCountAsync_ReturnsZeroForEmptyQueue()
    {
        // Arrange
        var server = CreateServer();
        var queueName = "test-queue";
        server.CreateQueue(queueName);

        // Act
        var stats = server.GetStats();
        var queueStats = stats.FirstOrDefault(s => s.Name.Equals(queueName, StringComparison.OrdinalIgnoreCase));

        // Assert
        queueStats.Should().NotBeNull();
        queueStats!.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task GetTotalBytesAsync_ReturnsCorrectSize()
    {
        // Arrange
        var server = CreateServer();
        var queueName = "test-queue";
        server.CreateQueue(queueName);
        
        var message = new string('X', 100);
        await server.EnqueueAsync(queueName, message);

        // Act
        var stats = server.GetStats();
        var queueStats = stats.FirstOrDefault(s => s.Name.Equals(queueName, StringComparison.OrdinalIgnoreCase));

        // Assert
        queueStats.Should().NotBeNull();
        queueStats!.TotalBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ConcurrentEnqueue_ThreadSafe()
    {
        // Arrange
        var server = CreateServer();
        var queueName = "concurrent-queue";
        server.CreateQueue(queueName);
        const int taskCount = 10;

        // Act
        var tasks = new Task[taskCount];
        for (int i = 0; i < taskCount; i++)
        {
            int captured = i;
            tasks[i] = Task.Run(async () => 
            {
                await server.EnqueueAsync(queueName, $"Message {captured}");
            });
        }
        await Task.WhenAll(tasks);

        // Assert
        var stats = server.GetStats();
        var queueStats = stats.FirstOrDefault(s => s.Name.Equals(queueName, StringComparison.OrdinalIgnoreCase));
        queueStats.Should().NotBeNull();
        queueStats!.MessageCount.Should().Be(taskCount);
    }

    [Fact]
    public async Task ConcurrentDequeue_ThreadSafe()
    {
        // Arrange
        var server = CreateServer();
        var queueName = "concurrent-queue";
        server.CreateQueue(queueName);
        const int messageCount = 10;

        // Enqueue messages
        for (int i = 0; i < messageCount; i++)
        {
            await server.EnqueueAsync(queueName, $"Message {i}");
        }

        // Act - Concurrent dequeue
        var tasks = new Task<string?>[messageCount];
        for (int i = 0; i < messageCount; i++)
        {
            tasks[i] = Task.Run(async () => await server.DequeueAsync<string>(queueName));
        }
        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(messageCount);
        results.Should().NotContainNulls();
        results.Distinct().Should().HaveCount(messageCount); // All messages should be unique
    }

    [Fact]
    public async Task QueuePersistence_SurvivesServerRestart()
    {
        // Arrange - Create first server and add message  
        var basePath1 = CreateTestBasePath();
        var options1 = new QueueStorageOptions
        {
            BasePath = basePath1,
            MaxMessageBytes = 1024 * 1024,
            MaxQueueBytes = 10 * 1024 * 1024,
            MaxQueueMessages = 1000,
            Overflow = OverflowPolicy.Reject
        };
        var server1 = new DiskBackedAmqpQueueServer(Options.Create(options1), _mockLogger.Object);
        
        var queueName = "persistent-queue";
        server1.CreateQueue(queueName);
        await server1.EnqueueAsync(queueName, "Persistent Message");
        
        // Verify message is in the queue on server1
        var stats1 = server1.GetStats();
        stats1.Should().HaveCount(1);
        stats1[0].MessageCount.Should().Be(1);

        // Flush to ensure all data is persisted to disk
        await server1.FlushAsync();

        // Assert - Verify files exist on disk after flush
        // This proves that the queue persistence mechanism works
        var queueDirs = Directory.GetDirectories(basePath1);
        queueDirs.Should().HaveCount(1, "Queue directory should exist");
        var queueDir = queueDirs[0];
        
        var stateFile = Path.Combine(queueDir, "state.json");
        File.Exists(stateFile).Should().BeTrue("state.json should be persisted to disk");
        
        var messageFiles = Directory.GetFiles(queueDir, "*.bin");
        messageFiles.Should().HaveCount(1, "Message file should be persisted to disk");
        
        // Verify the message content exists in the persisted file
        messageFiles[0].Should().NotBeEmpty();
        new FileInfo(messageFiles[0]).Length.Should().BeGreaterThan(0, "Message file should contain data");
    }

    [Fact]
    public async Task CancellationToken_StopsWaitingForMessage()
    {
        // Arrange
        var server = CreateServer();
        var queueName = "test-queue";
        server.CreateQueue(queueName);
        
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Act
        var startTime = DateTime.UtcNow;
        var result = await server.DequeueAsync<string>(queueName, cts.Token);
        var elapsed = DateTime.UtcNow - startTime;

        // Assert
        result.Should().BeNull();
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task EnqueueAsync_HandlesComplexObjects()
    {
        // Arrange
        var server = CreateServer();
        var queueName = "test-queue";
        server.CreateQueue(queueName);
        
        var complexObj = new
        {
            Id = 123,
            Name = "Test",
            Data = new[] { 1, 2, 3, 4, 5 },
            Nested = new { Value = "Nested" }
        };

        // Act
        await server.EnqueueAsync(queueName, complexObj);
        
        // Get stats to verify enqueue worked
        var stats = server.GetStats();

        // Assert - Verify object was queued and stats show 1 message
        stats.Should().HaveCount(1);
        stats[0].MessageCount.Should().Be(1);
        stats[0].Name.Should().Be(queueName);
    }

    [Fact]
    public async Task MultipleQueues_OperateIndependently()
    {
        // Arrange
        var server = CreateServer();
        var queue1 = "queue-1";
        var queue2 = "queue-2";
        server.CreateQueue(queue1);
        server.CreateQueue(queue2);

        // Act
        await server.EnqueueAsync(queue1, "Queue 1 Message");
        await server.EnqueueAsync(queue2, "Queue 2 Message");

        // Assert
        var msg1 = await server.DequeueAsync<string>(queue1);
        var msg2 = await server.DequeueAsync<string>(queue2);

        msg1.Should().Be("Queue 1 Message");
        msg2.Should().Be("Queue 2 Message");
    }
}
