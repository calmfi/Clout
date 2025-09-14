namespace Clout.UI.Services;

using Cloud.Shared;

public interface ICloutApiClient
{
    Task<IReadOnlyList<BlobInfo>> GetBlobsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<BlobInfo>> GetFunctionsAsync(CancellationToken ct = default);

    Task SetFunctionScheduleAsync(string id, string expression, CancellationToken ct = default);

    Task ClearFunctionScheduleAsync(string id, CancellationToken ct = default);
}
