# Clout

## Solution Layout

```
Clout.slnx
├─ Applications/
│  └─ Clout.Client/          # Console client for the API
├─ API/
│  └─ Clout.Host/            # Project: Clout.Host (ASP.NET Core Minimal API)
├─ Shared/
│  └─ Clout.Shared/          # Shared contracts (BlobInfo, BlobMetadata, IBlobStorage)
└─ Tests/
   └─ Clout.Host.IntegrationTests/ # Integration tests for API (folder: tests/Clout.Api.IntegrationTests)
```

## Common Commands

- Restore/build all: `dotnet build`
- Run API: `dotnet run --project Clout.Host/Clout.Host.csproj` (Swagger at `/swagger`)
- Run with Aspire (AppHost): `dotnet run --project Clout.AppHost` (UI + API with service discovery)
- Run client: `dotnet run --project Clout.Client -- <cmd>`
  - Blob commands (grouped under `blob`):
    - `blob list`, `blob info <id>`, `blob upload <path>`, `blob download <id> <dest>`, `blob delete <id>`,
      `blob metadata set <id> <name> <content-type> <value> [...]`
  - Function commands:
    - `functions register <dllPath> <name> [runtime=dotnet] [--cron <expr>]`,
      `functions schedule <id> <ncrontab>`, `functions unschedule <id>`,
      `functions cron-next <ncrontab> [count=5]`
- Test API: `dotnet test tests/Clout.Api.IntegrationTests/Clout.Host.IntegrationTests.csproj`

Tip: Under Aspire, the UI discovers the API dynamically (default base `http://clout-host`). For the console client, pass `--api <url>` when targeting a non-default API URL.

**Queue API** is now hosted inside `Clout.Host` (no separate `Clout.Queue` service). Endpoints appear in Swagger and share the same base URL.

## Queue API

- Health
  - `GET /health` — returns `OK`.
  - `GET /health/queues` — returns current queue stats.
- AMQP-like
  - `GET /amqp/queues` — list queues with stats.
  - `POST /amqp/queues/{name}` — create a queue (idempotent; 201).
  - `POST /amqp/queues/{name}/purge` — purge all messages.
  - `POST /amqp/queues/{name}/messages` — enqueue JSON body.
  - `POST /amqp/queues/{name}/dequeue?timeoutMs=...` — dequeue one message, optional timeout.

Examples (PowerShell):

```
# Create a queue
irm -Method Post http://localhost:5000/amqp/queues/demo

# Enqueue a JSON message
Invoke-RestMethod -Method Post -ContentType 'application/json' \
  -Body '{"hello":"world"}' \
  -Uri http://localhost:5000/amqp/queues/demo/messages

# Dequeue (wait up to 5 seconds)
irm -Method Post 'http://localhost:5000/amqp/queues/demo/dequeue?timeoutMs=5000'

# Stats
irm http://localhost:5000/health/queues
```

### Queue Configuration

- Configure via configuration key prefix `Queue:` (environment variables or configuration providers):
  - `Queue:BasePath` — base directory for queue data. If relative, resolved under `AppContext.BaseDirectory`.
  - `Queue:MaxQueueBytes` — per-queue max total bytes (optional).
  - `Queue:MaxQueueMessages` — per-queue max message count (optional).
  - `Queue:MaxMessageBytes` — per-message max size (optional).
  - `Queue:Overflow` — `Reject` (default) or `DropOldest` when quotas exceed.
  - `Queue:CleanupOrphansOnLoad` — `true`/`false` to delete stray `*.bin` files not referenced by state.

Example (Windows PowerShell):

```
$env:Queue__BasePath = 'queue-data'
$env:Queue__MaxQueueMessages = '10000'
dotnet run --project Clout.Host/Clout.Host.csproj
```

## Function Registration API

- Endpoint: `POST /api/functions/register`
- Purpose: Register a function by uploading its .NET Core entrypoint DLL.
- Request: `multipart/form-data`
  - `file`: The `.dll` file containing the function entrypoint.
  - `name`: Function name; a public method with this exact name must exist in the DLL.
  - `runtime`: Must be `.NET Core` (e.g., `dotnet`, `dotnetcore`).
- Behavior:
  - Loads the DLL in an isolated AssemblyLoadContext and verifies a public method named `name` exists.
  - On success, stores the DLL as a blob and sets metadata:
    - `function.name`, `function.runtime`, `function.entrypoint`, `function.declaringType`, `function.verified`.
  - Returns the resulting `BlobInfo` (including metadata) with 201 Created.

Example (PowerShell):

```
Invoke-RestMethod -Method Post -Uri http://localhost:5000/api/functions/register \
  -Form @{ file = Get-Item .\MyFunction.dll; name = 'MyFunction'; runtime = 'dotnet' }
```

Client example:

```
dotnet run --project Clout.Client -- functions register .\FunctionSamples.dll Echo dotnet

Register and schedule in one command:

```
dotnet run --project Clout.Client -- functions register .\FunctionSamples.dll Echo dotnet --cron "* * * * *"
```
```

Schedule a function (every minute):

```
dotnet run --project Clout.Client -- functions schedule <blobId> "* * * * *"
```

Clear schedule:

```
dotnet run --project Clout.Client -- functions unschedule <blobId>
```

Preview next run times for a cron expression (UTC):

```
dotnet run --project Clout.Client -- functions cron-next "* * * * *" 10

Notes:
- NCRONTAB supports 5-field (minute precision) and 6-field (seconds precision) expressions here. Both forms are accepted by the client and server.
```

## Blob CLI Examples

- List all blobs

```
dotnet run --project Clout.Client -- blob list
```

- Upload a file and show its info

```
dotnet run --project Clout.Client -- blob upload .\hello.txt
dotnet run --project Clout.Client -- blob list
dotnet run --project Clout.Client -- blob info <blobId>
```

- Download a blob

```
dotnet run --project Clout.Client -- blob download <blobId> .\out.txt
```

- Delete a blob

```
dotnet run --project Clout.Client -- blob delete <blobId>
```

- Set metadata on a blob

```
dotnet run --project Clout.Client -- blob metadata set <blobId> author text/plain alice
```

## Sample Function

- Build the sample function and schedule it to run every 10 seconds:

```
./scripts/register-sample-function.ps1 -Api http://localhost:5000
```

- The sample exposes a public method `Ping` in `Sample.Function.dll`. The registration script uses the client to upload the DLL and schedule it with NCRONTAB `*/10 * * * * *`.

## Queue CLI Usage examples

  - dotnet run --project Clout.Client -- queue list
  - dotnet run --project Clout.Client -- queue create demo
  - dotnet run --project Clout.Client -- queue enqueue demo "hello world"
  - dotnet run --project Clout.Client -- queue enqueue demo "{\"x\":1}" --as-json
  - dotnet run --project Clout.Client -- queue enqueue-file demo .\\payload.json application/json
  - dotnet run --project Clout.Client -- queue enqueue-file demo .\\image.png image/png
  - dotnet run --project Clout.Client -- queue dequeue demo --timeout-ms 5000