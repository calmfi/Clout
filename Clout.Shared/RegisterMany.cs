namespace Clout.Shared;

internal sealed class RegisterMany
{
    public string[] Names { get; set; } = Array.Empty<string>();
    public string? Runtime { get; set; } = "dotnet";
    public string? Cron { get; set; }
}
