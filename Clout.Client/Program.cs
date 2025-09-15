using System.Text.Json;
using Cloud.Shared;

// Cancellation: see AGENTS.md > "Cancellation & Async"
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
CancellationToken ct = cts.Token;
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
        case "blob":
            {
                if (args.Length < 2)
                {
                    return Fail("Usage: blob <list|info|upload|download|delete|metadata>");
                }

                var action = args[1].ToLowerInvariant();
                switch (action)
                {
                    case "list":
                        {
                            List<BlobInfo> blobs = await client.ListAsync(ct).ConfigureAwait(false);
                            foreach (BlobInfo b in blobs)
                            {
                                Console.WriteLine($"{b.Id}\t{b.Size} bytes\t{b.CreatedUtc:u}\t{b.FileName}");
                            }
                            return 0;
                        }
                    case "info":
                        {
                            if (args.Length < 3)
                            {
                                return Fail("Usage: blob info <id>");
                            }

                            BlobInfo? info = await client.GetInfoAsync(args[2], ct).ConfigureAwait(false);
                            if (info is null)
                            {
                                return Fail("Not found");
                            }

                            Console.WriteLine(JsonSerializer.Serialize(info, AppJsonContext.Default.BlobInfo));
                            return 0;
                        }
                    case "upload":
                        {
                            if (args.Length < 3)
                            {
                                return Fail("Usage: blob upload <filePath>");
                            }

                            BlobInfo result = await client.UploadAsync(args[2], cancellationToken: ct).ConfigureAwait(false);
                            Console.WriteLine($"Uploaded: {result.Id} -> {result.FileName} ({result.Size} bytes)");
                            return 0;
                        }
                    case "download":
                        {
                            if (args.Length < 4)
                            {
                                return Fail("Usage: blob download <id> <destPath>");
                            }

                            await client.DownloadAsync(args[2], args[3], ct).ConfigureAwait(false);
                            Console.WriteLine("Downloaded.");
                            return 0;
                        }
                    case "delete":
                        {
                            if (args.Length < 3)
                            {
                                return Fail("Usage: blob delete <id>");
                            }

                            var ok = await client.DeleteAsync(args[2], ct).ConfigureAwait(false);
                            Console.WriteLine(ok ? "Deleted." : "Not found.");
                            return ok ? 0 : 2;
                        }
                    case "metadata":
                        {
                            if (args.Length < 5)
                            {
                                return Fail("Usage: blob metadata set <id> <name> <content-type> <value> [<name> <content-type> <value> ...]");
                            }

                            var metaAction = args[2].ToLowerInvariant();
                            if (metaAction != "set")
                            {
                                return Fail("Only 'blob metadata set' is supported.");
                            }

                            var id = args[3];
                            if (((args.Length - 4) % 3) != 0 || args.Length < 7)
                            {
                                return Fail("Provide name/content-type/value triples after the id.");
                            }

                            var list = new List<BlobMetadata>();
                            for (var i = 4; i < args.Length; i += 3)
                            {
                                var name = args[i];
                                var ctType = args[i + 1];
                                var value = args[i + 2];
                                list.Add(new BlobMetadata(name, ctType, value));
                            }

                            BlobInfo updated = await client.SetMetadataAsync(id, list, ct).ConfigureAwait(false);
                            Console.WriteLine(JsonSerializer.Serialize(updated, AppJsonContext.Default.BlobInfo));
                            return 0;
                        }
                    default:
                        return Fail("Supported blob ops: list|info|upload|download|delete|metadata");
                }
            }
        case "functions":
            {
                var action = args[1].ToLowerInvariant();
                switch (action)
                {
                    case "cron-next":
                        {
                            if (args.Length < 3)
                            {
                                return Fail("Usage: functions cron-next <ncrontab> [count=5]");
                            }

                            var expr = args[2];
                            var count = 5;
                            if (args.Length >= 4 && !int.TryParse(args[3], out count))
                            {
                                return Fail("Count must be an integer.");
                            }
                            try
                            {
                                NCrontab.CrontabSchedule schedule;
                                try { schedule = NCrontab.CrontabSchedule.Parse(expr, new NCrontab.CrontabSchedule.ParseOptions { IncludingSeconds = true }); }
                                catch { schedule = NCrontab.CrontabSchedule.Parse(expr, new NCrontab.CrontabSchedule.ParseOptions { IncludingSeconds = false }); }
                                DateTime now = DateTime.UtcNow;
                                DateTime next = now;
                                for (var i = 0; i < count; i++)
                                {
                                    next = schedule.GetNextOccurrence(next);
                                    Console.WriteLine(next.ToString("u") + " UTC");
                                }
                                return 0;
                            }
                            catch (Exception ex)
                            {
                                return Fail($"Invalid NCRONTAB expression: {ex.Message}");
                            }
                        }
                    case "register":
                        {
                            if (args.Length < 4)
                            {
                                return Fail("Usage: functions register <dllPath> <name> [runtime=dotnet] [--cron <expr>]");
                            }

                            var dllPath = args[2];
                            var name = args[3];
                            string? runtime = null;
                            string? cron = null;

                            // Parse remaining args: either runtime as positional or --cron/-c flag
                            for (var i = 4; i < args.Length; i++)
                            {
                                var tok = args[i];
                                if (string.Equals(tok, "--cron", StringComparison.OrdinalIgnoreCase) || string.Equals(tok, "-c", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (i + 1 >= args.Length)
                                    {
                                        return Fail("Missing value after --cron");
                                    }

                                    cron = args[++i];
                                    continue;
                                }
                                // first non-flag becomes runtime if not already set
                                if (runtime is null) { runtime = tok; continue; }
                                return Fail("Too many arguments. Usage: functions register <dllPath> <name> [runtime=dotnet] [--cron <expr>]");
                            }
                            runtime ??= "dotnet";

                            BlobInfo result = cron is not null
                                ? await client.RegisterFunctionWithScheduleAsync(dllPath, name, cron, runtime, ct).ConfigureAwait(false)
                                : await client.RegisterFunctionAsync(dllPath, name, runtime, ct).ConfigureAwait(false);
                            Console.WriteLine(JsonSerializer.Serialize(result, AppJsonContext.Default.BlobInfo));
                            return 0;
                        }
                    case "register-scheduled":
                        {
                            if (args.Length < 5)
                            {
                                return Fail("Usage: functions register-scheduled <dllPath> <name> <ncrontab> [runtime=dotnet]");
                            }

                            var dllPath = args[2];
                            var name = args[3];
                            var cron = args[4];
                            var runtime = args.Length >= 6 ? args[5] : "dotnet";
                            BlobInfo result = await client.RegisterFunctionWithScheduleAsync(dllPath, name, cron, runtime, ct).ConfigureAwait(false);
                            Console.WriteLine(JsonSerializer.Serialize(result, AppJsonContext.Default.BlobInfo));
                            return 0;
                        }
                    case "register-from":
                        {
                            if (args.Length < 5)
                            {
                                return Fail("Usage: functions register-from <dllBlobId> <name> [runtime=dotnet]");
                            }

                            var dllId = args[2];
                            var name = args[3];
                            var runtime = args.Length >= 5 ? args[4] : "dotnet";
                            BlobInfo result = await client.RegisterFunctionFromExistingAsync(dllId, name, runtime, ct).ConfigureAwait(false);
                            Console.WriteLine(JsonSerializer.Serialize(result, AppJsonContext.Default.BlobInfo));
                            return 0;
                        }
                    case "register-many-from":
                        {
                            if (args.Length < 5)
                            {
                                return Fail("Usage: functions register-many-from <dllBlobId> <name1> [<name2> ...] [--runtime <r>] [--cron <expr>]");
                            }

                            var dllId = args[2];
                            var names = new List<string>();
                            var runtime = "dotnet";
                            string? cron = null;
                            for (var i = 3; i < args.Length; i++)
                            {
                                var tok = args[i];
                                if (string.Equals(tok, "--runtime", StringComparison.OrdinalIgnoreCase) || string.Equals(tok, "-r", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (i + 1 >= args.Length)
                                    {
                                        return Fail("Missing value after --runtime");
                                    }

                                    runtime = args[++i];
                                    continue;
                                }
                                if (string.Equals(tok, "--cron", StringComparison.OrdinalIgnoreCase) || string.Equals(tok, "-c", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (i + 1 >= args.Length)
                                    {
                                        return Fail("Missing value after --cron");
                                    }

                                    cron = args[++i];
                                    continue;
                                }
                                names.Add(tok);
                            }
                            if (names.Count == 0)
                            {
                                return Fail("Provide at least one function name.");
                            }

                            List<BlobInfo> result = await client.RegisterFunctionsFromExistingAsync(dllId, names, runtime, cron, ct).ConfigureAwait(false);
                            Console.WriteLine(JsonSerializer.Serialize(result, AppJsonContext.Default.ListBlobInfo));
                            return 0;
                        }
                    case "register-many":
                        {
                            if (args.Length < 4)
                            {
                                return Fail("Usage: functions register-many <dllPath> <name1> [<name2> ...] [--runtime <r>] [--cron <expr>]");
                            }

                            var dllPath = args[2];
                            var names = new List<string>();
                            var runtime = "dotnet";
                            string? cron = null;
                            for (var i = 3; i < args.Length; i++)
                            {
                                var tok = args[i];
                                if (string.Equals(tok, "--runtime", StringComparison.OrdinalIgnoreCase) || string.Equals(tok, "-r", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (i + 1 >= args.Length)
                                    {
                                        return Fail("Missing value after --runtime");
                                    }

                                    runtime = args[++i];
                                    continue;
                                }
                                if (string.Equals(tok, "--cron", StringComparison.OrdinalIgnoreCase) || string.Equals(tok, "-c", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (i + 1 >= args.Length)
                                    {
                                        return Fail("Missing value after --cron");
                                    }

                                    cron = args[++i];
                                    continue;
                                }
                                names.Add(tok);
                            }
                            if (names.Count == 0)
                            {
                                return Fail("Provide at least one function name.");
                            }

                            List<BlobInfo> result = await client.RegisterFunctionsAsync(dllPath, names, runtime, cron, ct).ConfigureAwait(false);
                            Console.WriteLine(JsonSerializer.Serialize(result, AppJsonContext.Default.ListBlobInfo));
                            return 0;
                        }
                    case "schedule":
                        {
                            if (args.Length < 4)
                            {
                                return Fail("Usage: functions schedule <id> <ncrontab>");
                            }

                            var id = args[2];
                            var cron = args[3];
                            BlobInfo result = await client.SetTimerTriggerAsync(id, cron, ct).ConfigureAwait(false);
                            Console.WriteLine(JsonSerializer.Serialize(result, AppJsonContext.Default.BlobInfo));
                            return 0;
                        }
                    case "unschedule":
                    case "clear-schedule":
                        {
                            if (args.Length < 3)
                            {
                                return Fail("Usage: functions unschedule <id>");
                            }

                            var id = args[2];
                            BlobInfo result = await client.ClearTimerTriggerAsync(id, ct).ConfigureAwait(false);
                            Console.WriteLine(JsonSerializer.Serialize(result, AppJsonContext.Default.BlobInfo));
                            return 0;
                        }
                    default:
                        return Fail("Supported: functions register|register-many|register-from|register-many-from|schedule|unschedule");
                }
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
catch (ArgumentException ex)
{
    return Fail(ex.Message);
}
catch (Exception ex)
{
    return Fail($"Error: {ex.Message}");
}

int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

void PrintUsage()
{
    Console.WriteLine("clout client for Local Cloud API");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  clout blob list");
    Console.WriteLine("  clout blob info <id>");
    Console.WriteLine("  clout blob upload <filePath>");
    Console.WriteLine("  clout blob download <id> <destPath>");
    Console.WriteLine("  clout blob delete <id>");
    Console.WriteLine("  clout blob metadata set <id> <name> <content-type> <value> [<name> <content-type> <value> ...]");
    Console.WriteLine("  clout functions register <dllPath> <name> [runtime=dotnet] [--cron <expr>]");
    Console.WriteLine("  clout functions register-many <dllPath> <name1> [<name2> ...] [--runtime <r>] [--cron <expr>]");
    Console.WriteLine("  clout functions register-from <dllBlobId> <name> [runtime=dotnet]");
    Console.WriteLine("  clout functions register-many-from <dllBlobId> <name1> [<name2> ...] [--runtime <r>] [--cron <expr>]");
    Console.WriteLine("  clout functions schedule <id> <ncrontab>");
    Console.WriteLine("  clout functions unschedule <id>");
    Console.WriteLine("  clout functions cron-next <ncrontab> [count=5]");
    Console.WriteLine();
    Console.WriteLine("Set API base with CLOUT_API (default http://localhost:5000)");
}
// Types moved to separate files.
