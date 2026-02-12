using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clout.Host.Storage;
using Clout.Shared.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Clout.ServiceTests.Storage;

/// <summary>
/// Service-level tests for FileBlobStorage implementation.
/// Tests focus on business logic, error handling, and service behavior.
/// </summary>
public class FileBlobStorageTests : IDisposable
{
    private readonly string _testRoot;
    private readonly Mock<ILogger<FileBlobStorage>> _mockLogger;
    private readonly FileBlobStorage _storage;

    public FileBlobStorageTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"clout-test-{Guid.NewGuid():N}");
        _mockLogger = new Mock<ILogger<FileBlobStorage>>();
        _storage = new FileBlobStorage(_testRoot, _mockLogger.Object);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Cleanup is best effort
        }
    }

    [Fact]
    public async Task SaveAsync_CreatesNewBlobWithMetadata()
    {
        // Arrange
        var content = new MemoryStream(Encoding.UTF8.GetBytes("Test content"));
        var fileName = "test.txt";
        var contentType = "text/plain";

        // Act
        var result = await _storage.SaveAsync(fileName, content, contentType);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().NotBeNullOrEmpty();
        result.FileName.Should().Be(fileName);
        result.ContentType.Should().Be(contentType);
        result.Size.Should().Be(12); // "Test content".Length
        result.CreatedUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SaveAsync_PersistsContentToDisk()
    {
        // Arrange
        var testData = "Persisted test data";
        var content = new MemoryStream(Encoding.UTF8.GetBytes(testData));

        // Act
        var result = await _storage.SaveAsync("test.txt", content, "text/plain");
        var stream = await _storage.OpenReadAsync(result.Id);

        // Assert
        stream.Should().NotBeNull();
        using var reader = new StreamReader(stream!);
        var readData = await reader.ReadToEndAsync();
        readData.Should().Be(testData);
    }

    [Fact]
    public async Task SaveAsync_HandlesEmptyContent()
    {
        // Arrange
        var content = new MemoryStream(Array.Empty<byte>());

        // Act
        var result = await _storage.SaveAsync("empty.txt", content, "text/plain");

        // Assert
        result.Should().NotBeNull();
        result.Size.Should().Be(0);
    }

    [Fact]
    public async Task SaveAsync_HandlesLargeContent()
    {
        // Arrange
        var largeData = new byte[10 * 1024 * 1024]; // 10 MB
        new Random().NextBytes(largeData);
        var content = new MemoryStream(largeData);

        // Act
        var result = await _storage.SaveAsync("large.bin", content, "application/octet-stream");

        // Assert
        result.Should().NotBeNull();
        result.Size.Should().Be(largeData.Length);
    }

    [Fact]
    public async Task ReplaceAsync_UpdatesExistingBlob()
    {
        // Arrange
        var originalContent = new MemoryStream(Encoding.UTF8.GetBytes("Original"));
        var saved = await _storage.SaveAsync("test.txt", originalContent, "text/plain");
        
        var newContent = new MemoryStream(Encoding.UTF8.GetBytes("Updated content"));

        // Act
        var result = await _storage.ReplaceAsync(saved.Id, newContent, "text/plain", "test.txt");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(saved.Id);
        result.Size.Should().Be(15); // "Updated content".Length
        
        // Verify content was actually replaced
        var stream = await _storage.OpenReadAsync(saved.Id);
        using var reader = new StreamReader(stream!);
        var readData = await reader.ReadToEndAsync();
        readData.Should().Be("Updated content");
    }

    [Fact]
    public async Task ReplaceAsync_ReturnsNullForNonExistentBlob()
    {
        // Arrange
        var content = new MemoryStream(Encoding.UTF8.GetBytes("Test"));
        var nonExistentId = Guid.NewGuid().ToString("N");

        // Act
        var result = await _storage.ReplaceAsync(nonExistentId, content, "text/plain", "test.txt");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task ReplaceAsync_UpdatesContentType()
    {
        // Arrange
        var content = new MemoryStream(Encoding.UTF8.GetBytes("Test"));
        var saved = await _storage.SaveAsync("test.txt", content, "text/plain");
        
        var newContent = new MemoryStream(Encoding.UTF8.GetBytes("Test"));

        // Act
        var result = await _storage.ReplaceAsync(saved.Id, newContent, "application/json", null);

        // Assert
        result.Should().NotBeNull();
        result!.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task OpenReadAsync_ReturnsStreamForExistingBlob()
    {
        // Arrange
        var testData = "Stream test data";
        var content = new MemoryStream(Encoding.UTF8.GetBytes(testData));
        var saved = await _storage.SaveAsync("test.txt", content, "text/plain");

        // Act
        var stream = await _storage.OpenReadAsync(saved.Id);

        // Assert
        stream.Should().NotBeNull();
        stream!.CanRead.Should().BeTrue();
        
        using var reader = new StreamReader(stream);
        var readData = await reader.ReadToEndAsync();
        readData.Should().Be(testData);
    }

    [Fact]
    public async Task OpenReadAsync_ReturnsNullForNonExistentBlob()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid().ToString("N");

        // Act
        var stream = await _storage.OpenReadAsync(nonExistentId);

        // Assert
        stream.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ReturnsEmptyListWhenNoBlobs()
    {
        // Act
        var result = await _storage.ListAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_ReturnsAllBlobs()
    {
        // Arrange
        var content1 = new MemoryStream(Encoding.UTF8.GetBytes("Content 1"));
        var content2 = new MemoryStream(Encoding.UTF8.GetBytes("Content 2"));
        var content3 = new MemoryStream(Encoding.UTF8.GetBytes("Content 3"));
        
        await _storage.SaveAsync("file1.txt", content1, "text/plain");
        await Task.Delay(100); // Ensure different timestamps
        await _storage.SaveAsync("file2.txt", content2, "text/plain");
        await Task.Delay(100);
        await _storage.SaveAsync("file3.txt", content3, "text/plain");

        // Act
        var result = await _storage.ListAsync();

        // Assert
        result.Should().HaveCount(3);
        result.Select(b => b.FileName).Should().Contain(new[] { "file1.txt", "file2.txt", "file3.txt" });
    }

    [Fact]
    public async Task ListAsync_ReturnsBlobsInDescendingOrderByCreatedDate()
    {
        // Arrange
        var content1 = new MemoryStream(Encoding.UTF8.GetBytes("First"));
        var content2 = new MemoryStream(Encoding.UTF8.GetBytes("Second"));
        
        await _storage.SaveAsync("first.txt", content1, "text/plain");
        await Task.Delay(100);
        await _storage.SaveAsync("second.txt", content2, "text/plain");

        // Act
        var result = await _storage.ListAsync();

        // Assert
        result.Should().HaveCount(2);
        result[0].FileName.Should().Be("second.txt");
        result[1].FileName.Should().Be("first.txt");
    }

    [Fact]
    public async Task GetInfoAsync_ReturnsMetadataForExistingBlob()
    {
        // Arrange
        var content = new MemoryStream(Encoding.UTF8.GetBytes("Test"));
        var saved = await _storage.SaveAsync("test.txt", content, "text/plain");

        // Act
        var result = await _storage.GetInfoAsync(saved.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(saved.Id);
        result.FileName.Should().Be("test.txt");
        result.ContentType.Should().Be("text/plain");
        result.Size.Should().Be(4);
    }

    [Fact]
    public async Task GetInfoAsync_ReturnsNullForNonExistentBlob()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid().ToString("N");

        // Act
        var result = await _storage.GetInfoAsync(nonExistentId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesExistingBlob()
    {
        // Arrange
        var content = new MemoryStream(Encoding.UTF8.GetBytes("Test"));
        var saved = await _storage.SaveAsync("test.txt", content, "text/plain");

        // Act
        var result = await _storage.DeleteAsync(saved.Id);

        // Assert
        result.Should().BeTrue();
        
        // Verify blob is actually deleted
        var info = await _storage.GetInfoAsync(saved.Id);
        info.Should().BeNull();
        
        var stream = await _storage.OpenReadAsync(saved.Id);
        stream.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ReturnsFalseForNonExistentBlob()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid().ToString("N");

        // Act
        var result = await _storage.DeleteAsync(nonExistentId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task SetMetadataAsync_UpdatesMetadataForExistingBlob()
    {
        // Arrange
        var content = new MemoryStream(Encoding.UTF8.GetBytes("Test"));
        var saved = await _storage.SaveAsync("test.txt", content, "text/plain");
        
        var metadata = new List<BlobMetadata>
        {
            new("key1","text", "value1"),
            new("key2", "text", "value2")
        };

        // Act
        var result = await _storage.SetMetadataAsync(saved.Id, metadata);

        // Assert
        result.Should().NotBeNull();
        result!.Metadata.Should().HaveCount(2);
        result.Metadata.Should().Contain(m => m.Name == "key1" && m.Value == "value1");
        result.Metadata.Should().Contain(m => m.Name == "key2" && m.Value == "value2");
        
        // Verify metadata persists
        var info = await _storage.GetInfoAsync(saved.Id);
        info!.Metadata.Should().HaveCount(2);
    }

    [Fact]
    public async Task SetMetadataAsync_ReturnsNullForNonExistentBlob()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid().ToString("N");
        var metadata = new List<BlobMetadata> { new("key", "text", "value") };

        // Act
        var result = await _storage.SetMetadataAsync(nonExistentId, metadata);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SetMetadataAsync_ReplacesExistingMetadata()
    {
        // Arrange
        var content = new MemoryStream(Encoding.UTF8.GetBytes("Test"));
        var saved = await _storage.SaveAsync("test.txt", content, "text/plain");
        
        var metadata1 = new List<BlobMetadata> { new("old", "text", "value") };
        await _storage.SetMetadataAsync(saved.Id, metadata1);
        
        var metadata2 = new List<BlobMetadata> { new("new", "text", "value") };

        // Act
        var result = await _storage.SetMetadataAsync(saved.Id, metadata2);

        // Assert
        result.Should().NotBeNull();
        result!.Metadata.Should().HaveCount(1);
        result.Metadata[0].Name.Should().Be("new");
    }

    [Fact]
    public async Task CancellationToken_PropagatesCorrectly()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var content = new MemoryStream(Encoding.UTF8.GetBytes("Test"));

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await _storage.SaveAsync("test.txt", content, "text/plain", cts.Token)
        );
    }

    [Fact]
    public async Task MultipleOperations_WorkCorrectly()
    {
        // Arrange & Act
        var saved1 = await _storage.SaveAsync("file1.txt", 
            new MemoryStream(Encoding.UTF8.GetBytes("Content 1")), "text/plain");
        var saved2 = await _storage.SaveAsync("file2.txt", 
            new MemoryStream(Encoding.UTF8.GetBytes("Content 2")), "text/plain");
        
        await _storage.ReplaceAsync(saved1.Id, 
            new MemoryStream(Encoding.UTF8.GetBytes("Updated")), "text/plain", "file1.txt");
        
        await _storage.DeleteAsync(saved2.Id);
        
        var list = await _storage.ListAsync();

        // Assert
        list.Should().HaveCount(1);
        list[0].Id.Should().Be(saved1.Id);
        
        var stream = await _storage.OpenReadAsync(saved1.Id);
        using var reader = new StreamReader(stream!);
        var content = await reader.ReadToEndAsync();
        content.Should().Be("Updated");
    }
}
