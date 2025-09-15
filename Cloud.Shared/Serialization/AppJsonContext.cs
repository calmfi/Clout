using System.Text.Json.Serialization;
using Cloud.Shared.Models;

namespace Cloud.Shared;

/// <summary>
/// Provides a source-generated JSON serialization context for the application.
/// This context includes serialization metadata for various types such as <see cref="BlobInfo"/>,
/// <see cref="BlobMetadata"/>, and collections like <see cref="List{T}"/> and <see cref="Dictionary{TKey, TValue}"/>.
/// </summary>

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BlobInfo))]
[JsonSerializable(typeof(List<BlobInfo>))]
[JsonSerializable(typeof(BlobMetadata))]
[JsonSerializable(typeof(List<BlobMetadata>))]
[JsonSerializable(typeof(IEnumerable<BlobMetadata>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class AppJsonContext : JsonSerializerContext { }
