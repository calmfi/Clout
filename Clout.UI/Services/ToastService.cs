namespace Clout.UI.Services;

public enum ToastLevel
{
    Info,
    Success,
    Warning,
    Error
}

public sealed class ToastMessage
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public ToastLevel Level { get; init; }
    public int DurationMs { get; init; } = 3000;
}

public sealed class ToastService
{
    private readonly List<ToastMessage> _messages = new();
    public event Action? OnChange;

    public IReadOnlyList<ToastMessage> Messages => _messages.ToList();

    public string Show(string text, ToastLevel level = ToastLevel.Info, int durationMs = 3000)
    {
        var id = Guid.NewGuid().ToString("N");
        var msg = new ToastMessage { Id = id, Text = text, Level = level, DurationMs = durationMs };
        _messages.Add(msg);
        OnChange?.Invoke();
        _ = AutoDismissAsync(id, durationMs);
        return id;
    }

    public void Dismiss(string id)
    {
        var idx = _messages.FindIndex(m => m.Id == id);
        if (idx >= 0)
        {
            _messages.RemoveAt(idx);
            OnChange?.Invoke();
        }
    }

    private async Task AutoDismissAsync(string id, int durationMs)
    {
        try
        {
            await Task.Delay(durationMs);
            Dismiss(id);
        }
        catch
        {
            // ignore
        }
    }
}

