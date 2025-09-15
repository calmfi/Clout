using Cloud.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Clout.UI.Components.Pages;

public partial class Blobs
{
    private readonly List<BlobRow> _blobs = new();
    private List<BlobRow> _view = new();
    private bool _loading;
    private string? _error;
    private string _query = string.Empty;
    [Inject] private BlobApiClient Api { get; set; } = default!;
    [Inject] private AppConfig Config { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _loading = true;
            _error = null;
            _blobs.Clear();
            var items = await Api.ListAsync();
            _blobs.AddRange(items
                .OrderByDescending(b => b.CreatedUtc)
                .Select(ToRow));
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private Task OnSearch(string text)
    {
        _query = text ?? string.Empty;
        ApplyFilter();
        return Task.CompletedTask;
    }

    private void ClearFilter()
    {
        _query = string.Empty;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = _query.Trim();
        _view = string.IsNullOrEmpty(q)
            ? _blobs.ToList()
            : _blobs.Where(r =>
                    (r.FileName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (r.Id?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        StateHasChanged();
    }

    private async Task DeleteAsync(string id)
    {
        try
        {
            if (!await Confirm($"Delete blob {id}?")) return;
            if (await Api.DeleteAsync(id))
            {
                var idx = _blobs.FindIndex(b => b.Id == id);
                if (idx >= 0) _blobs.RemoveAt(idx);
                ApplyFilter();
                ShowToast("Blob deleted.");
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private bool _metaOpen;
    private string _metaTitle = string.Empty;
    private List<BlobMetadata> _meta = new();
    private string _metaBlobId = string.Empty;
    private bool _metaEdit;
    private List<EditMetaRow> _metaEditList = new();

    private async Task ShowMetadataAsync(string id)
    {
        try
        {
            _error = null;
            var info = await Api.GetInfoAsync(id);
            if (info is null)
            {
                _error = "Blob not found";
                return;
            }

            _metaBlobId = info.Id;
            _metaTitle = $"Metadata: {info.FileName} ({info.Id})";
            _meta = info.Metadata ?? new List<BlobMetadata>();
            _metaOpen = true;
            _metaEdit = false;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private void CloseMetadata()
    {
        _metaOpen = false;
    }

    private void BeginEdit()
    {
        _metaEdit = true;
        _metaEditList = _meta.Select(m => new EditMetaRow
            { Name = m.Name, ContentType = m.ContentType, Value = m.Value }).ToList();
    }

    private async Task SaveEditAsync()
    {
        try
        {
            var payload = _metaEditList
                .Where(m => !string.IsNullOrWhiteSpace(m.Name))
                .Select(m => new BlobMetadata(m.Name.Trim(),
                    string.IsNullOrWhiteSpace(m.ContentType) ? "text/plain" : m.ContentType.Trim(),
                    m.Value ?? string.Empty))
                .ToList();
            var updated = await Api.SetMetadataAsync(_metaBlobId, payload);
            _meta = updated.Metadata ?? new List<BlobMetadata>();
            _metaEdit = false;
            var row = _blobs.FirstOrDefault(b => b.Id == _metaBlobId);
            if (row is not null)
            {
                row.MetadataCount = _meta.Count;
                ApplyFilter();
            }

            ShowToast("Metadata saved.");
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private void CancelEdit() => _metaEdit = false;

    private void AddMetaRow()
    {
        _metaEditList.Add(new EditMetaRow { Name = string.Empty, ContentType = "text/plain", Value = string.Empty });
        ShowToast("Row added.");
    }

    private void RemoveMetaRow(EditMetaRow row)
    {
        _metaEditList.Remove(row);
        ShowToast("Row removed.");
    }

    private void HandleKeyDown(KeyboardEventArgs e)
    {
        if (string.Equals(e.Key, "Escape", StringComparison.OrdinalIgnoreCase))
            CloseMetadata();
    }

    private static BlobRow ToRow(BlobInfo b) => new()
    {
        Id = b.Id,
        FileName = b.FileName,
        SizeBytes = b.Size,
        SizeDisplay = HumanSize(b.Size),
        ContentType = b.ContentType ?? string.Empty,
        CreatedUtc = b.CreatedUtc,
        CreatedDisplay = b.CreatedUtc.ToString("u"),
        MetadataCount = b.Metadata?.Count ?? 0
    };

    private static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int u = 0;
        while (size >= 1024 && u < units.Length - 1)
        {
            size /= 1024;
            u++;
        }

        return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.##} {1}", size, units[u]);
    }

    private sealed class BlobRow
    {
        public string Id { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string SizeDisplay { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; set; }
        public string CreatedDisplay { get; set; } = string.Empty;
        public int MetadataCount { get; set; }
    }

    [Inject] private IJSRuntime JS { get; set; } = default!;

    private async Task CopyToClipboard(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            try
            {
                await JS.InvokeVoidAsync("navigator.clipboard.writeText", text);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    private async Task<bool> Confirm(string message)
    {
        try
        {
            return await JS.InvokeAsync<bool>("confirm", message);
        }
        catch
        {
            return true;
        }
    }

    private sealed class EditMetaRow
    {
        public string Name { get; init; } = string.Empty;
        public string ContentType { get; init; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private bool _toastVisible;
    private string _toastMessage = string.Empty;

    private void ShowToast(string message)
    {
        _toastMessage = message;
        _toastVisible = true;
        _ = HideToastAfterDelay();
    }

    private async Task HideToastAfterDelay()
    {
        await Task.Delay(2500);
        _toastVisible = false;
        await InvokeAsync(StateHasChanged);
    }
}