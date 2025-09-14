# Clout

## Solution Layout

```
Clout.slnx
├─ Applications/
│  ├─ Clout.UI/              # Blazor Server app with Fluent UI
│  └─ Clout.Client/          # Console client for the API
├─ API/
│  └─ Clout.Api/             # ASP.NET Core Minimal API (Swagger enabled)
├─ Shared/
│  └─ Cloud.Shared/          # Shared contracts (BlobInfo, BlobMetadata, IBlobStorage)
└─ Tests/
   └─ Clout.Api.IntegrationTests/  # Integration tests for API
```

## Common Commands

- Restore/build all: `dotnet build`
- Run API: `dotnet run --project Clout.Api` (Swagger at `/swagger`)
- Run Blazor app: `dotnet run --project Clout.UI`
- Run client: `dotnet run --project Clout.Client -- <cmd>`
  - Blob commands (grouped under `blob`):
    - `blob list`, `blob info <id>`, `blob upload <path>`, `blob download <id> <dest>`, `blob delete <id>`,
      `blob metadata set <id> <name> <content-type> <value> [...]`
  - Function commands:
    - `functions register <dllPath> <name> [runtime=dotnet] [--cron <expr>]`,
      `functions schedule <id> <ncrontab>`, `functions unschedule <id>`,
      `functions cron-next <ncrontab> [count=5]`
- Test API: `dotnet test tests/Clout.Api.IntegrationTests/Clout.Api.IntegrationTests.csproj`

Tip: Set `CLOUT_API` to point the client at a non-default base URL (default `http://localhost:5000`).

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
