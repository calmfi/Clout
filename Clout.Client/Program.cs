using Cloud.Shared;

namespace Clout.Client;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        // Cancellation: see AGENTS.md > "Cancellation & Async"
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var ct = cts.Token;
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var apiBase = Environment.GetEnvironmentVariable("CLOUT_API") ?? "http://localhost:5000";
        var client = new BlobApiClient(apiBase);

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "metadata":
                    {
                        if (args.Length < 3) return Fail("Usage: metadata set <id> <name> <content-type> <value> [<name> <content-type> <value> ...]");
                        var action = args[1].ToLowerInvariant();
                        if (action != "set") return Fail("Only 'metadata set' is supported.");
                        var id = args[2];
                        if (((args.Length - 3) % 3) != 0 || args.Length < 6)
                            return Fail("Provide name/content-type/value triples after the id.");

                        var list = new List<BlobMetadata>();
                        for (int i = 3; i < args.Length; i += 3)
                        {
                            var name = args[i];
                            var ctType = args[i + 1];
                            var value = args[i + 2];
                            list.Add(new BlobMetadata(name, ctType, value));
                        }

                        var updated = await client.SetMetadataAsync(id, list, ct);
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(updated, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                        return 0;
                    }
                case "list":
                    {
                        var blobs = await client.ListAsync(ct);
                        foreach (var b in blobs)
                        {
                            Console.WriteLine($"{b.Id}\t{b.Size} bytes\t{b.CreatedUtc:u}\t{b.FileName}");
                        }
                        return 0;
                    }
                case "info":
                    {
                        if (args.Length < 2) return Fail("Usage: info <id>");
                        var info = await client.GetInfoAsync(args[1], ct);
                        if (info is null) return Fail("Not found");
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                        return 0;
                    }
                case "upload":
                    {
                        if (args.Length < 2) return Fail("Usage: upload <filePath>");
                        var result = await client.UploadAsync(args[1], cancellationToken: ct);
                        Console.WriteLine($"Uploaded: {result.Id} -> {result.FileName} ({result.Size} bytes)");
                        return 0;
                    }
                case "download":
                    {
                        if (args.Length < 3) return Fail("Usage: download <id> <destPath>");
                        await client.DownloadAsync(args[1], args[2], ct);
                        Console.WriteLine("Downloaded.");
                        return 0;
                    }
                case "delete":
                    {
                        if (args.Length < 2) return Fail("Usage: delete <id>");
                        var ok = await client.DeleteAsync(args[1], ct);
                        Console.WriteLine(ok ? "Deleted." : "Not found.");
                        return ok ? 0 : 2;
                    }
                default:
                    PrintUsage();
                    return 1;
            }
        }
        catch (HttpRequestException ex)
        {
            return Fail($"HTTP error: {ex.Message}");
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("clout client for Local Cloud API");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  clout list");
        Console.WriteLine("  clout info <id>");
        Console.WriteLine("  clout upload <filePath>");
        Console.WriteLine("  clout download <id> <destPath>");
        Console.WriteLine("  clout delete <id>");
        Console.WriteLine("  clout metadata set <id> <name> <content-type> <value> [<name> <content-type> <value> ...]");
        Console.WriteLine();
        Console.WriteLine("Set API base with CLOUT_API (default http://localhost:5000)");
    }
}

// Types moved to separate files.

