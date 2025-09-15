namespace Cloud.Shared.Models;

/// <summary>
/// Represents a single blob metadata tuple: name, content type, and value.
/// </summary>
public record BlobMetadata
{
    /// <summary>Metadata key/name.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Metadata content type (e.g., text/plain, application/json).</summary>
    public string ContentType { get; init; } = string.Empty;
    /// <summary>Metadata value serialized as text.</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>Initializes a new instance.</summary>
    public BlobMetadata() { }

    /// <summary>Initializes a new instance.</summary>
    public BlobMetadata(string name, string contentType, string value)
    {
        Name = name;
        ContentType = contentType;
        Value = value;
    }
}

