using Clout.Shared;
using Clout.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Clout.UI.Components.Pages;

public partial class Functions
{
    private static readonly string[] NameSplitSeparators = new[] { ",", ";", "\n", "\r" };
    private readonly List<FunctionRow> _functions = new();
    private List<FunctionRow> _view = new();
    private bool _loading;
    private string? _error;
    private string _query = string.Empty;
    private bool _queueOpen;
    private string _queueId = string.Empty;
    private string _queueName = string.Empty;
    private string? _queueError;
    private FunctionRow? _queueRow;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private AppConfig Config { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync().ConfigureAwait(true);
        // Pre-fill register-many dialog if query contains from=<id>
        var uri = new Uri(Nav.Uri);
        var qs = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var from = qs["from"];
        if (!string.IsNullOrWhiteSpace(from))
        {
            _regExistingId = from!;
            OpenRegisterManyDialog();
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            _loading = true;
            _error = null;
            _functions.Clear();
            var items = await Api.ListAsync().ConfigureAwait(true);
            _functions.AddRange(items.Where(IsFunction)
                .Select(ToRow)
                .OrderByDescending(r => r.CreatedUtc));
            ApplyFilter();
        }
        catch (HttpRequestException ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private static bool IsFunction(BlobInfo b)
    {
        return b.Metadata?.Any(m => m.Name == "function.name" || m.Name == "function.runtime") == true;
    }

    private static FunctionRow ToRow(BlobInfo b) => new()
    {
        Id = b.Id,
        CreatedUtc = b.CreatedUtc,
        CreatedDisplay = b.CreatedUtc.ToString("u"),
        Name = Meta(b, "function.name"),
        Runtime = Meta(b, "function.runtime"),
        Entrypoint = Meta(b, "function.entrypoint"),
        DeclaringType = Meta(b, "function.declaringType"),
        Verified = Meta(b, "function.verified"),
        TimerTrigger = Meta(b, "TimerTrigger"),
        QueueTrigger = Meta(b, "QueueTrigger"),
        SourceId = Meta(b, "function.sourceId")
    };

    private static string Meta(BlobInfo b, string key) =>
        b.Metadata?.FirstOrDefault(m => string.Equals(m.Name, key, StringComparison.OrdinalIgnoreCase))?.Value ??
        string.Empty;

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
        if (string.IsNullOrEmpty(q))
        {
            _view = _functions.ToList();
        }
        else
        {
            _view = _functions.Where(r =>
                    (r.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (r.Id?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        StateHasChanged();
    }

    private sealed class FunctionRow
    {
        public string Id { get; set; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; set; }
        public string CreatedDisplay { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Runtime { get; set; } = string.Empty;
        public string Entrypoint { get; set; } = string.Empty;
        public string DeclaringType { get; set; } = string.Empty;
        public string Verified { get; set; } = string.Empty;
        public string TimerTrigger { get; set; } = string.Empty;
        public string QueueTrigger { get; set; } = string.Empty;
        public string? SourceId { get; set; }
    }

    private async Task UnscheduleAsync(FunctionRow r)
    {
        try
        {
            if (!await Confirm($"Unschedule TimerTrigger for {r.Name} ({r.Id})?").ConfigureAwait(true)) return;
            var updated = await Api.ClearTimerTriggerAsync(r.Id).ConfigureAwait(true);
            r.TimerTrigger = string.Empty;
            StateHasChanged();
            ShowToast("Function unscheduled.");
        }
        catch (HttpRequestException ex)
        {
            _error = ex.Message;
        }
    }


    private void OpenQueueDialog(FunctionRow row)
    {
        _queueRow = row;
        _queueId = row.Id;
        _queueName = row.QueueTrigger ?? string.Empty;
        _queueError = null;
        _queueOpen = true;
        StateHasChanged();
    }

    private void CloseQueueDialog()
    {
        _queueOpen = false;
        _queueRow = null;
        _queueError = null;
        StateHasChanged();
    }

    private async Task SaveQueueAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_queueName))
            {
                _queueError = "Provide a queue name.";
                return;
            }

            var queue = _queueName.Trim();
            var updated = await Api.SetQueueTriggerAsync(_queueId, queue).ConfigureAwait(true);
            _queueName = queue;
            var value = Meta(updated, "QueueTrigger");
            if (_queueRow is not null)
            {
                _queueRow.QueueTrigger = value;
            }
            else
            {
                var match = _functions.FirstOrDefault(f => string.Equals(f.Id, _queueId, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    match.QueueTrigger = value;
                }
            }

            ApplyFilter();
            CloseQueueDialog();
            ShowToast($"Bound queue '{_queueName}'.");
        }
        catch (HttpRequestException ex)
        {
            _queueError = ex.Message;
        }
        catch (ArgumentException ex)
        {
            _queueError = ex.Message;
        }
    }

    private async Task ClearQueueAsync(FunctionRow row)
    {
        try
        {
            if (!await Confirm($"Unbind queue trigger for {row.Name} ({row.Id})?").ConfigureAwait(true)) return;
            var updated = await Api.ClearQueueTriggerAsync(row.Id).ConfigureAwait(true);
            row.QueueTrigger = Meta(updated, "QueueTrigger");
            if (_queueRow is not null && string.Equals(_queueRow.Id, row.Id, StringComparison.OrdinalIgnoreCase))
            {
                CloseQueueDialog();
            }
            ApplyFilter();
            ShowToast("Queue trigger cleared.");
        }
        catch (HttpRequestException ex)
        {
            _error = ex.Message;
        }
    }

    private void HandleQueueKeyDown(KeyboardEventArgs e)
    {
        if (string.Equals(e.Key, "Escape", StringComparison.OrdinalIgnoreCase))
        {
            CloseQueueDialog();
        }
    }


    private async Task DeleteAsync(string id)
    {
        try
        {
            if (!await Confirm($"Unregister function (delete blob) {id}?").ConfigureAwait(true)) return;
            if (await Api.DeleteAsync(id).ConfigureAwait(true))
            {
                var idx = _functions.FindIndex(b => b.Id == id);
                if (idx >= 0) _functions.RemoveAt(idx);
                ApplyFilter();
                ShowToast("Function unregistered.");
            }
        }
        catch (HttpRequestException ex)
        {
            _error = ex.Message;
        }
    }

    [Inject] private IJSRuntime JS { get; set; } = default!;

    private async Task<bool> Confirm(string message)
    {
        try
        {
            return await JS.InvokeAsync<bool>("confirm", message).ConfigureAwait(true);
        }
        catch (JSException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
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
        await Task.Delay(2500).ConfigureAwait(true);
        _toastVisible = false;
        await InvokeAsync(StateHasChanged).ConfigureAwait(true);
    }

    private bool _schedOpen;
    private string _schedId = string.Empty;
    private string _schedName = string.Empty;
    private string _schedCron = string.Empty;
    private string? _schedError;

    private void OpenScheduleDialog(FunctionRow r)
    {
        _schedId = r.Id;
        _schedName = r.Name;
        _schedCron = string.IsNullOrWhiteSpace(r.TimerTrigger) ? string.Empty : r.TimerTrigger;
        _schedError = null;
        _schedOpen = true;
    }

    private void CloseScheduleDialog()
    {
        _schedOpen = false;
    }

    private async Task SaveScheduleAsync()
    {
        try
        {
            _schedError = null;
            if (string.IsNullOrWhiteSpace(_schedCron))
            {
                _schedError = "Cron expression is required.";
                return;
            }

            if (!ApiClient.IsValidCron(_schedCron))
            {
                _schedError = "Invalid NCRONTAB expression.";
                return;
            }

            var updated = await Api.SetTimerTriggerAsync(_schedId, _schedCron).ConfigureAwait(true);
            var row = _functions.FirstOrDefault(f => f.Id == _schedId);
            if (row is not null)
            {
                row.TimerTrigger = _schedCron;
                ApplyFilter();
            }

            _schedOpen = false;
            ShowToast("Function scheduled.");
        }
        catch (HttpRequestException ex)
        {
            _schedError = ex.Message;
        }
    }

    private bool _regOpen;
    private IBrowserFile? _regFile;
    private string _regExistingId = string.Empty;
    private string _regNames = string.Empty;
    private string _regRuntime = "dotnet";
    private string _regCron = string.Empty;
    private FunctionTriggerKind _regTrigger = FunctionTriggerKind.Timer;
    private string _regQueueName = string.Empty;
    private readonly List<string> _cronPreview = new();
    private bool _perRow;
    private readonly List<FuncRow> _rows = new();
    private string? _regError;
    private string _regTriggerValue
    {
        get => _regTrigger.ToString();
        set
        {
            if (Enum.TryParse<FunctionTriggerKind>(value, true, out var parsed))
            {
                if (_regTrigger != parsed)
                {
                    _regTrigger = parsed;
                    OnRegisterTriggerChanged(parsed);
                }
            }
        }
    }
    private bool _reAllOpen;
    private string _reAllSourceId = string.Empty;
    private string _reAllCron = string.Empty;
    private string? _reAllError;

    private void OpenRegisterManyDialog()
    {
        _regOpen = true;
        _regFile = null;
        _regNames = string.Empty;
        _regRuntime = "dotnet";
        _regCron = string.Empty;
        _regTrigger = FunctionTriggerKind.Timer;
        _regQueueName = string.Empty;
        _cronPreview.Clear();
        _perRow = false;
        _rows.Clear();
        _regError = null;
    }

    private void CloseRegisterManyDialog() => _regOpen = false;

    private void OnRegisterTriggerChanged(FunctionTriggerKind kind)
    {
        if (kind == FunctionTriggerKind.Queue)
        {
            if (_perRow)
            {
                _perRow = false;
                _rows.Clear();
            }
            _regCron = string.Empty;
            _cronPreview.Clear();
        }
        else if (kind == FunctionTriggerKind.Timer)
        {
            _regQueueName = string.Empty;
        }

        StateHasChanged();
    }

    private void HandleRegisterKeyDown(KeyboardEventArgs e)
    {
        if (string.Equals(e.Key, "Escape", StringComparison.OrdinalIgnoreCase)) _regOpen = false;
    }

    private void OnDllSelected(InputFileChangeEventArgs e)
    {
        _regFile = e.File;
    }

    private async void ComputeCronPreview()
    {

        if (_regTrigger != FunctionTriggerKind.Timer)
        {
            _cronPreview.Clear();
            StateHasChanged(); return;
        }
        _cronPreview.Clear();
        var expr = _regCron?.Trim();
        if (string.IsNullOrWhiteSpace(expr))
        {
            StateHasChanged();
            return;
        }

        try
        {
            _cronPreview.AddRange(await Api.CronNextAsync(expr, 5).ConfigureAwait(true));
        }
        catch (HttpRequestException)
        {
        }
        catch (ArgumentException)
        {
        }

        StateHasChanged();
    }

    private void SyncRowsFromNames()
    {
        if (_regTrigger != FunctionTriggerKind.Timer)
        {
            return;
        }
        var set = new HashSet<string>(_rows.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var n in (_regNames ?? string.Empty).Split(NameSplitSeparators,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (set.Contains(n)) continue;
            _rows.Add(new FuncRow { Name = n, Cron = string.Empty });
        }

        StateHasChanged();
    }

    private void AddFuncRow()
    {
        _rows.Add(new FuncRow());
        StateHasChanged();
    }

    private void RemoveFuncRow(FuncRow row)
    {
        _rows.Remove(row);
        StateHasChanged();
    }

    private sealed class FuncRow
    {
        public string Name { get; set; } = string.Empty;
        public string? Cron { get; set; }
    }
    private enum FunctionTriggerKind
    {
        Timer,
        Queue
    }


    private void OpenRescheduleAllDialog(string sourceId, string anyName)
    {
        _reAllSourceId = sourceId;
        _reAllCron = string.Empty;
        _reAllError = null;
        _reAllOpen = true;
    }

    private void CloseRescheduleAllDialog() => _reAllOpen = false;

    private async Task SaveRescheduleAllAsync()
    {
        try
        {
            _reAllError = null;
            if (string.IsNullOrWhiteSpace(_reAllCron))
            {
                _reAllError = "Cron expression is required.";
                return;
            }

            if (!ApiClient.IsValidCron(_reAllCron))
            {
                _reAllError = "Invalid NCRONTAB expression.";
                return;
            }

            var count = await Api.ScheduleAllAsync(_reAllSourceId, _reAllCron).ConfigureAwait(true);
            foreach (var f in _functions.Where(f =>
                         string.Equals(f.SourceId, _reAllSourceId, StringComparison.OrdinalIgnoreCase)))
            {
                f.TimerTrigger = _reAllCron;
            }

            ApplyFilter();
            _reAllOpen = false;
            ShowToast($"Rescheduled {count} functions.");
        }
        catch (HttpRequestException ex)
        {
            _reAllError = ex.Message;
        }
    }

    private async Task UnscheduleAllAsync(string sourceId)
    {
        try
        {
            if (!await Confirm($"Unschedule all functions from DLL {sourceId}?").ConfigureAwait(true)) return;
            var count = await Api.UnscheduleAllAsync(sourceId).ConfigureAwait(true);
            foreach (var f in _functions.Where(f =>
                         string.Equals(f.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)))
            {
                f.TimerTrigger = string.Empty;
            }

            ApplyFilter();
            ShowToast($"Unscheduled {count} functions.");
        }
        catch (HttpRequestException ex)
        {
            _error = ex.Message;
        }
    }

    private async Task SaveRegisterManyAsync()
    {
        try
        {
            _regError = null;
            if (_regFile is null && string.IsNullOrWhiteSpace(_regExistingId))
            {
                _regError = "Select a DLL file or provide an existing Blob Id.";
                return;
            }

            var trigger = _regTrigger;
            var queueName = (_regQueueName ?? string.Empty).Trim();

            if (trigger == FunctionTriggerKind.Queue && string.IsNullOrWhiteSpace(queueName))
            {
                _regError = "Provide a queue name.";
                return;
            }

            var names = (_regNames ?? string.Empty)
                .Split(NameSplitSeparators,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (trigger == FunctionTriggerKind.Timer)
            {
                if (!_perRow && !string.IsNullOrWhiteSpace(_regCron) && !ApiClient.IsValidCron(_regCron))
                {
                    _regError = "Invalid NCRONTAB expression.";
                    return;
                }

                if (_perRow)
                {
                    foreach (var r in _rows)
                    {
                        if (!string.IsNullOrWhiteSpace(r.Cron) && !ApiClient.IsValidCron(r.Cron))
                        {
                            _regError = $"Invalid cron for '{r.Name}'.";
                            return;
                        }
                    }
                }
            }

            if (names.Length == 0 && _rows.Count == 0)
            {
                _regError = "Provide at least one function name.";
                return;
            }

            if (names.Length == 0)
            {
                names = _rows.Select(r => r.Name).Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }

            string? cronToSend = null;
            if (trigger == FunctionTriggerKind.Timer && !_perRow && !string.IsNullOrWhiteSpace(_regCron))
            {
                cronToSend = _regCron;
            }

            List<BlobInfo> result;
            if (!string.IsNullOrWhiteSpace(_regExistingId))
            {
                result = await Api.RegisterFunctionsFromExistingAsync(_regExistingId!, names, _regRuntime,
                    cronToSend).ConfigureAwait(true);
            }
            else
            {
                using var stream = _regFile!.OpenReadStream(50 * 1024 * 1024); // 50 MB limit
                result = await Api.RegisterFunctionsAsync(stream, _regFile!.Name, names, _regRuntime,
                    cronToSend).ConfigureAwait(true);
            }

            if (trigger == FunctionTriggerKind.Timer && _perRow && result.Count == names.Length)
            {
                var map = _rows.Where(r => !string.IsNullOrWhiteSpace(r.Cron))
                    .ToDictionary(r => r.Name, r => r.Cron!, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < result.Count; i++)
                {
                    if (map.TryGetValue(names[i], out var c))
                    {
                        try
                        {
                            await Api.SetTimerTriggerAsync(result[i].Id, c).ConfigureAwait(true);
                        }
                        catch (HttpRequestException)
                        {
                        }
                        catch (ArgumentException)
                        {
                        }
                    }
                }
            }

            string toast;
            if (trigger == FunctionTriggerKind.Queue)
            {
                _regQueueName = queueName;
                await BindQueueTriggersAsync(result, queueName).ConfigureAwait(true);
                toast = await BuildQueueToastAsync(queueName, result.Count).ConfigureAwait(true);
            }
            else
            {
                toast = $"Registered {result.Count} functions.";
            }

            _regOpen = false;
            ShowToast(toast);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (HttpRequestException ex)
        {
            _regError = ex.Message;
        }
    }
    private async Task BindQueueTriggersAsync(IReadOnlyList<BlobInfo> items, string queueName)
    {
        foreach (var blob in items)
        {
            try
            {
                await Api.SetQueueTriggerAsync(blob.Id, queueName).ConfigureAwait(true);
            }
            catch (HttpRequestException)
            {
            }
            catch (ArgumentException)
            {
            }
        }
    }

    private async Task<string> BuildQueueToastAsync(string queueName, int count)
    {
        var stats = await TryGetQueueStatsAsync(queueName).ConfigureAwait(true);
        if (stats is null)
        {
            return $"Registered {count} queue-triggered functions for queue '{queueName}'.";
        }

        var pending = stats.MessageCount;
        var suffix = pending == 1 ? "message" : "messages";
        var availability = pending > 0 ? $"{pending} pending {suffix}" : "no pending messages";
        return $"Registered {count} queue-triggered functions. Queue '{queueName}' currently has {availability}.";
    }

    private async Task<QueueStats?> TryGetQueueStatsAsync(string queueName)
    {
        try
        {
            var queues = await Api.ListQueuesAsync().ConfigureAwait(true);
            return queues.FirstOrDefault(q => string.Equals(q.Name, queueName, StringComparison.OrdinalIgnoreCase));
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
    private void HandleScheduleKeyDown(KeyboardEventArgs e)
    {
        if (string.Equals(e.Key, "Escape", StringComparison.OrdinalIgnoreCase))
            CloseScheduleDialog();
    }

    private void HandleRescheduleAllKeyDown(KeyboardEventArgs e)
    {
        if (string.Equals(e.Key, "Escape", StringComparison.OrdinalIgnoreCase))
            CloseRescheduleAllDialog();
    }
}


