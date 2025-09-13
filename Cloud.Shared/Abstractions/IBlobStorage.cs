namespace Cloud.Shared;

/// <summary>
/// Abstraction for blob storage operations.
/// </summary>
public interface IBlobStorage
{
    /// <summary>
    /// Saves a new blob to storage.
    /// </summary>
    /// <param name="originalName">Original filename from the client.</param>
    /// <param name="content">Stream containing the blob bytes.</param>
    /// <param name="contentType">Optional MIME type.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Metadata of the newly saved blob.</returns>
    Task<BlobInfo> SaveAsync(string originalName, Stream content, string? contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets metadata for a blob.
    /// </summary>
    /// <param name="id">Blob identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Blob metadata, or null if not found.</returns>
    Task<BlobInfo?> GetInfoAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a readable stream for a blob's content.
    /// </summary>
    /// <param name="id">Blob identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Readable stream, or null if not found.</returns>
    Task<Stream?> OpenReadAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all blobs in storage.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of blob metadata.</returns>
    Task<IReadOnlyList<BlobInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a blob.
    /// </summary>
    /// <param name="id">Blob identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if a blob was deleted; otherwise false.</returns>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an existing blob's content, and optionally its filename and content type.
    /// </summary>
    /// <param name="id">Blob identifier.</param>
    /// <param name="content">New content stream.</param>
    /// <param name="contentType">Optional MIME type to set.</param>
    /// <param name="originalName">Optional new filename to set.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated metadata, or null if the blob does not exist.</returns>
    Task<BlobInfo?> ReplaceAsync(string id, Stream content, string? contentType, string? originalName, CancellationToken cancellationToken = default);
}

