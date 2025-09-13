namespace Cloud.Shared;

/// <summary>
/// Describes a stored blob and its metadata.
/// </summary>
public record BlobInfo
{
    /// <summary>
    /// The unique identifier for the blob.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// The original filename associated with the blob.
    /// </summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// Size of the blob in bytes.
    /// </summary>
    public long Size { get; init; }

    /// <summary>
    /// UTC timestamp when the blob was created.
    /// </summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    /// Optional MIME content type for the blob.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Arbitrary metadata entries associated with this blob.
    /// Each entry carries a name, content type, and string value.
    /// </summary>
    public List<BlobMetadata> Metadata { get; init; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobInfo"/> record.
    /// </summary>
    public BlobInfo() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobInfo"/> record.
    /// </summary>
    /// <param name="id">Unique identifier for the blob.</param>
    /// <param name="fileName">Original filename associated with the blob.</param>
    /// <param name="size">Size in bytes.</param>
    /// <param name="createdUtc">Creation time in UTC.</param>
    /// <param name="contentType">Optional MIME content type.</param>
    public BlobInfo(string id, string fileName, long size, DateTimeOffset createdUtc, string? contentType)
    {
        Id = id;
        FileName = fileName;
        Size = size;
        CreatedUtc = createdUtc;
        ContentType = contentType;
    }
}
