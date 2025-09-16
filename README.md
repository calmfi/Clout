# Clout

Local, file-backed building blocks (blobs, queues, scheduled functions) exposed via an ASP.NET Core Minimal API + console client + optional Aspire AppHost + lightweight UI.

---
## Solution Layout

```text
Clout.slnx
├─ Clout.Host/              # ASP.NET Core Minimal API (blobs, queues, functions, scheduling)
├─ Clout.Client/            # Console CLI for interacting with the API
├─ Clout.Shared/            # Shared contracts & client (`BlobInfo`, `BlobMetadata`, `BlobApiClient`, queue + function models)
├─ Clout.ServiceDefaults/   # Shared service defaults (Aspire wiring)
├─ Clout.AppHost/           # .NET Aspire AppHost (runs API + UI together)
├─ Clout.UI/                # UI (discovers API via Aspire or configured base URL)
├─ samples/Sample.Function/ # Example function assembly
└─ tests/Clout.Api.IntegrationTests/ # Integration tests
```

## Quick Start

```bash
dotnet build
dotnet run --project Clout.Host/Clout.Host.csproj        # API at http://localhost:5000 (Swagger: /swagger)
# In a second terminal:
dotnet run --project Clout.Client -- blob list
```

Run with Aspire (API + UI):

```bash
dotnet run --project Clout.AppHost
```

Pass a custom API base to the client:

```bash
dotnet run --project Clout.Client -- --api http://localhost:5001 blob list
```

## CLI Overview

All commands: `dotnet run --project Clout.Client -- <group> <action> ...`

Groups:
- `blob` (list, info, upload, download, delete, metadata set)
- `queue` (list, create, purge, enqueue, enqueue-file, dequeue)
- `functions` (list, register, register-many, register-from, register-many-from, schedule, unschedule, cron-next)

Use `Ctrl+C` to cancel long operations (cancellation wired throughout).

## Blob Commands

List blobs:

```bash
dotnet run --project Clout.Client -- blob list
```

Upload a file:

```bash
dotnet run --project Clout.Client -- blob upload .\hello.txt
```

Show info:

```bash
dotnet run --project Clout.Client -- blob info <blobId>
```

Download:

```bash
dotnet run --project Clout.Client -- blob download <blobId> .\out.txt
```

Delete:

```bash
dotnet run --project Clout.Client -- blob delete <blobId>
```

Set metadata (triples of name / content-type / value):

```bash
dotnet run --project Clout.Client -- blob metadata set <blobId> author text/plain alice
```

## Queue Commands (CLI)

```bash
dotnet run --project Clout.Client -- queue list
dotnet run --project Clout.Client -- queue create demo
dotnet run --project Clout.Client -- queue enqueue demo "hello world"
dotnet run --project Clout.Client -- queue enqueue demo "{\"x\":1}" --as-json
dotnet run --project Clout.Client -- queue enqueue-file demo .\payload.json application/json
dotnet run --project Clout.Client -- queue enqueue-file demo .\image.png image/png
dotnet run --project Clout.Client -- queue dequeue demo --timeout-ms 5000
dotnet run --project Clout.Client -- queue purge demo
```

## Queue HTTP API

Health / stats:

- `GET /health` → plain text `OK`.
- `GET /health/queues` → JSON queue stats.

AMQP‑like endpoints (JSON messages):

- `GET /amqp/queues` — list queues + stats
- `POST /amqp/queues/{name}` — create queue (201)
- `POST /amqp/queues/{name}/purge` — purge all messages
- `POST /amqp/queues/{name}/messages` — enqueue JSON body
- `POST /amqp/queues/{name}/dequeue?timeoutMs=5000` — dequeue (204 if none)

PowerShell examples:

```powershell
irm -Method Post http://localhost:5000/amqp/queues/demo              # create
Invoke-RestMethod -Method Post -ContentType 'application/json' `
  -Body '{"hello":"world"}' `
  -Uri  http://localhost:5000/amqp/queues/demo/messages             # enqueue
irm -Method Post 'http://localhost:5000/amqp/queues/demo/dequeue?timeoutMs=3000'
irm http://localhost:5000/health/queues                              # stats
```

### Queue Configuration

Environment / config keys (prefix `Queue:`):

- `Queue:BasePath` (string, relative resolves under app base dir)
- `Queue:MaxQueueBytes` (long, optional)
- `Queue:MaxQueueMessages` (int, optional)
- `Queue:MaxMessageBytes` (int, optional)
- `Queue:Overflow` (`Reject` | `DropOldest`, default `Reject`)
- `Queue:CleanupOrphansOnLoad` (`true`/`false`)

PowerShell example:

```powershell
$env:Queue__BasePath = 'queue-data'
$env:Queue__MaxQueueMessages = '10000'
dotnet run --project Clout.Host/Clout.Host.csproj
```

## Function Commands (CLI)

Register a single function:

```bash
dotnet run --project Clout.Client -- functions register .\FunctionSamples.dll Echo
```

Register & schedule in one call (Quartz cron 5 or 6 fields; seconds optional):

```bash
dotnet run --project Clout.Client -- functions register .\FunctionSamples.dll Echo --cron "* * * * *"
```

Register many from a DLL:

```bash
dotnet run --project Clout.Client -- functions register-many .\FunctionSamples.dll Echo Ping --runtime dotnet --cron "*/5 * * * * *"
```

Register from existing DLL blob id:

```bash
dotnet run --project Clout.Client -- functions register-from <dllBlobId> Echo
```

Register many from existing DLL blob id:

```bash
dotnet run --project Clout.Client -- functions register-many-from <dllBlobId> Echo Ping --cron "0 */2 * * * *"
```

List registered functions:

```bash
dotnet run --project Clout.Client -- functions list
```

Schedule / reschedule:

```bash
dotnet run --project Clout.Client -- functions schedule <functionBlobId> "*/10 * * * * *"
```

Unschedule:

```bash
dotnet run --project Clout.Client -- functions unschedule <functionBlobId>
```

Preview upcoming times (UTC):

```bash
dotnet run --project Clout.Client -- functions cron-next "*/15 * * * * *" 8
```

## Function HTTP API (selected)

`POST /api/functions/register` — upload DLL (multipart: file,name,runtime)
`POST /api/functions/register-many` — upload DLL with names list (multipart: file,names,runtime[,cron])
`POST /api/functions/register-from/{dllBlobId}` — register from existing blob (JSON: name,runtime)
`POST /api/functions/register-many-from/{dllBlobId}` — register many (JSON: names[],runtime,cron?)
`POST /api/functions/{id}/schedule` — set cron (JSON: expression)
`DELETE /api/functions/{id}/schedule` — clear schedule
`GET /api/functions` — list function blobs
`GET /api/functions/cron-next?expr=...&count=5` — preview times

Additional bulk scheduling:
`POST /api/functions/schedule-all` (JSON: sourceId, cron)
`POST /api/functions/unschedule-all` (JSON: sourceId)

## Cron Expressions

Quartz compatible. 5-field expressions are auto-expanded to 6 (seconds=0). Both `*/10 * * * * *` (with seconds) and `*/10 * * * *` (without) are accepted. Validation performed client-side and server-side.

## Sample Function Workflow

Build & register the sample, schedule every 10 seconds:


```powershell
./scripts/register-sample-function.ps1 -Api http://localhost:5000
```

Script registers method `Ping` in the sample assembly and applies cron `*/10 * * * * *`.

## Testing

Run integration tests:

```bash
dotnet test tests/Clout.Api.IntegrationTests/Clout.Host.IntegrationTests.csproj
```

## Cancellation & Async

All async APIs accept a `CancellationToken`. The console client binds `Ctrl+C` to a shared token and propagates it to every network call and I/O operation. Server endpoints pass the request-aborted token into downstream operations.

## Environment / Configuration Notes

- Blob & queue storage are file-backed under the application's working/output directory.
- Provide overrides via environment variables or `appsettings.*.json`.
- For client against non-default base: supply `--api <url>` (must precede command group).

## Contributing

Use Conventional Commits (`feat:`, `fix:`, `docs:`, `test:`, etc.). Run `dotnet format` before submitting PRs. Avoid adding solution (`.sln`) files—project uses `Clout.slnx`.

## License

TBD
