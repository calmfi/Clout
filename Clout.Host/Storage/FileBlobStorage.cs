using System.Text.Json;
using Cloud.Shared.Abstractions;
using Cloud.Shared.Models;

namespace Clout.Host.Storage;

/// <summary>
/// File-system based implementation of <see cref="IBlobStorage"/>, storing content and metadata under a root folder.
/// </summary>
public sealed class FileBlobStorage : IBlobStorage
{
    private readonly string _root;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>
    /// Creates a new <see cref="FileBlobStorage"/> rooted at the specified directory.
    /// </summary>
    /// <param name="root">Root directory for blob files and metadata.</param>
    public FileBlobStorage(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    /// <inheritdoc />
    public async Task<BlobInfo> SaveAsync(string originalName, Stream content, string? contentType, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var (bin, meta) = Paths(id);

        if (!Directory.Exists(_root))
        {
            Directory.CreateDirectory(_root);
        }
        await using (var fs = File.Create(bin))
        {
            await content.CopyToAsync(fs, cancellationToken);
        }

        var info = await BuildInfoAsync(id, originalName, contentType);
        await WriteMetaAsync(meta, info, cancellationToken);
        return info;
    }

    /// <inheritdoc />
    public async Task<BlobInfo?> ReplaceAsync(string id, Stream content, string? contentType, string? originalName, CancellationToken cancellationToken = default)
    {
        var (bin, meta) = Paths(id);
        if (!File.Exists(bin) || !File.Exists(meta)) return null;

        if (!Directory.Exists(_root))
        {
            Directory.CreateDirectory(_root);
        }
        await using (var fs = File.Create(bin))
        {
            await content.CopyToAsync(fs, cancellationToken);
        }

        var existing = await ReadMetaAsync(meta, cancellationToken);
        var info = existing is null
            ? await BuildInfoAsync(id, originalName ?? id, contentType)
            : existing with
            {
                FileName = originalName ?? existing.FileName,
                ContentType = contentType ?? existing.ContentType,
                Size = new FileInfo(bin).Length
            };

        await WriteMetaAsync(meta, info, cancellationToken);
        return info;
    }

    /// <inheritdoc />
    public Task<Stream?> OpenReadAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (bin, _) = Paths(id);
        if (!File.Exists(bin)) return Task.FromResult<Stream?>(null);
        return Task.FromResult<Stream?>(File.OpenRead(bin));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BlobInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root))
        {
            return new List<BlobInfo>();
        }
        var results = new List<BlobInfo>();
        foreach (var meta in Directory.EnumerateFiles(_root, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = await ReadMetaAsync(meta, cancellationToken);
            if (info is not null) results.Add(info);
        }
        return results.OrderByDescending(b => b.CreatedUtc).ToList();
    }

    /// <inheritdoc />
    public async Task<BlobInfo?> GetInfoAsync(string id, CancellationToken cancellationToken = default)
    {
        var (_, meta) = Paths(id);
        return await ReadMetaAsync(meta, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (bin, meta) = Paths(id);
        var existed = File.Exists(bin) || File.Exists(meta);
        try
        {
            if (File.Exists(bin)) File.Delete(bin);
            if (File.Exists(meta)) File.Delete(meta);
            return Task.FromResult(existed);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    public async Task<BlobInfo?> SetMetadataAsync(string id, IReadOnlyList<BlobMetadata> metadata, CancellationToken cancellationToken = default)
    {
        var (bin, metaPath) = Paths(id);
        if (!File.Exists(metaPath)) return null;
        var existing = await ReadMetaAsync(metaPath, cancellationToken);
        if (existing is null) return null;
        var updated = existing with { Metadata = metadata.ToList() };
        await WriteMetaAsync(metaPath, updated, cancellationToken);
        return updated;
    }

    private (string bin, string meta) Paths(string id)
    {
        var bin = Path.Combine(_root, $"{id}.bin");
        var meta = Path.Combine(_root, $"{id}.json");
        return (bin, meta);
    }

    private async Task<BlobInfo> BuildInfoAsync(string id, string fileName, string? contentType)
    {
        var (bin, _) = Paths(id);
        var size = new FileInfo(bin).Length;
        return new BlobInfo(id, fileName, size, DateTimeOffset.UtcNow, contentType);
    }

    private async Task<BlobInfo?> ReadMetaAsync(string metaPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(metaPath)) return null;
        await using var fs = File.OpenRead(metaPath);
        return await System.Text.Json.JsonSerializer.DeserializeAsync<BlobInfo>(fs, _jsonOptions, cancellationToken);
    }

    private async Task WriteMetaAsync(string metaPath, BlobInfo info, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(metaPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await using var fs = File.Create(metaPath);
        await System.Text.Json.JsonSerializer.SerializeAsync(fs, info, _jsonOptions, cancellationToken);
    }
}

