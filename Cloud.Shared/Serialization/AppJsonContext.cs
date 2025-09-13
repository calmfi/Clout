using System.Text.Json.Serialization;

namespace Cloud.Shared;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BlobInfo))]
[JsonSerializable(typeof(List<BlobInfo>))]
[JsonSerializable(typeof(BlobMetadata))]
[JsonSerializable(typeof(List<BlobMetadata>))]
[JsonSerializable(typeof(IEnumerable<BlobMetadata>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class AppJsonContext : JsonSerializerContext { }
