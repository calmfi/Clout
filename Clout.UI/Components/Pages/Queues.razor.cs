using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Text.Json;

namespace Clout.UI.Components.Pages
{
    public partial class Queues : IDisposable
    {
        private sealed class QueueRow
        {
            public string Name { get; set; } = string.Empty;
            public int MessageCount { get; set; }
            public long TotalBytes { get; set; }
            public string TotalBytesDisplay => FormatBytes(TotalBytes);
        }

        private sealed class MessageRow
        {
            public DateTimeOffset Timestamp { get; set; }
            public string Raw { get; set; } = string.Empty;
            public string Display { get; set; } = string.Empty;
        }

        [Inject] private Clout.Shared.BlobApiClient Api { get; set; } = default!;
        [Inject] private IJSRuntime JS { get; set; } = default!;

        private readonly List<QueueRow> _queues = new();
        private readonly List<MessageRow> _messages = new();
        private string _queueName = string.Empty;
        private string _enqueuePayload = string.Empty;
        private bool _asJson;
        private string? _error;
        private bool _dequeueLoading;
        private bool _autoRefresh = true;
        private string _refreshMs = "3000";
        private CancellationTokenSource? _pollCts;

        protected override async Task OnInitializedAsync()
        {
            await LoadStatsAsync();
            StartAutoRefresh();
        }

        private async Task LoadStatsAsync()
        {
            try
            {
                _error = null;
                var stats = await Api.ListQueuesAsync().ConfigureAwait(true);
                _queues.Clear();
                _queues.AddRange(stats.OrderBy(s => s.Name).Select(s => new QueueRow
                {
                    Name = s.Name,
                    MessageCount = s.MessageCount,
                    TotalBytes = s.TotalBytes
                }));
            }
            catch (HttpRequestException ex)
            {
                _error = ex.Message;
            }
            StateHasChanged();
        }

        private async Task CreateQueueAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_queueName)) return;
                _error = null;
                await Api.CreateQueueAsync(_queueName).ConfigureAwait(true);
                ShowToast("Queue created.");
                await LoadStatsAsync().ConfigureAwait(true);
            }
            catch (HttpRequestException ex) { _error = ex.Message; }
        }

        private async Task PurgeQueueAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_queueName)) return;
                _error = null;
                await Api.PurgeQueueAsync(_queueName).ConfigureAwait(true);
                ShowToast("Queue purged.");
                await LoadStatsAsync().ConfigureAwait(true);
            }
            catch (HttpRequestException ex) { _error = ex.Message; }
        }

        private async Task EnqueueAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_queueName) || string.IsNullOrWhiteSpace(_enqueuePayload)) return;
                _error = null;
                if (_asJson)
                {
                    using var doc = JsonDocument.Parse(_enqueuePayload);
                    await Api.EnqueueJsonAsync(_queueName, doc.RootElement).ConfigureAwait(true);
                }
                else
                {
                    await Api.EnqueueStringAsync(_queueName, _enqueuePayload, asJson: false).ConfigureAwait(true);
                }
                _enqueuePayload = string.Empty;
                ShowToast("Enqueued.");
                await LoadStatsAsync().ConfigureAwait(true);
            }
            catch (JsonException)
            {
                _error = "Invalid JSON payload.";
            }
            catch (HttpRequestException ex) { _error = ex.Message; }
        }

        private async Task DequeueAsync()
        {
            if (string.IsNullOrWhiteSpace(_queueName)) return;
            try
            {
                _dequeueLoading = true;
                var elem = await Api.DequeueAsync(_queueName, timeoutMs: 50).ConfigureAwait(true);
                if (elem is null)
                {
                    ShowToast("(no message)");
                }
                else
                {
                    var raw = elem.Value.GetRawText();
                    string display;
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        display = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
                    }
                    catch { display = raw; }

                    _messages.Insert(0, new MessageRow
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        Raw = raw,
                        Display = display
                    });
                    if (_messages.Count > 50) _messages.RemoveRange(50, _messages.Count - 50);
                }
                await LoadStatsAsync().ConfigureAwait(true);
            }
            catch (HttpRequestException ex) { _error = ex.Message; }
            finally { _dequeueLoading = false; }
        }

        private static string FormatBytes(long b)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double val = b;
            int u = 0;
            while (val >= 1024 && u < units.Length - 1) { val /= 1024; u++; }
            return $"{val:0.##} {units[u]}";
        }

        private async Task CopyAsync(string text)
        {
            try { await JS.InvokeVoidAsync("navigator.clipboard.writeText", text); ShowToast("Copied."); } catch { }
        }

        private void StartAutoRefresh()
        {
            _pollCts?.Cancel();
            if (!_autoRefresh) return;
            if (!int.TryParse(_refreshMs, out var ms) || ms < 500) ms = 3000;
            _pollCts = new CancellationTokenSource();
            var token = _pollCts.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(ms, token).ConfigureAwait(false); } catch { break; }
                    if (token.IsCancellationRequested) break;
                    try { await InvokeAsync(LoadStatsAsync); } catch { }
                }
            }, token);
        }

        private bool _toastVisible;
        private string _toastMessage = string.Empty;
        private void ShowToast(string msg)
        {
            _toastMessage = msg;
            _toastVisible = true;
            _ = HideToastAsync();
        }
        private async Task HideToastAsync()
        {
            await Task.Delay(2500);
            _toastVisible = false;
            await InvokeAsync(StateHasChanged);
        }

        protected override void OnParametersSet()
        {
            StartAutoRefresh();
        }

        public void Dispose()
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
        }
    }
}