# Clout

## Solution Layout

```
Clout.slnx
├─ Applications/
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
- Run client: `dotnet run --project Clout.Client -- <cmd>`
  - Examples: `list`, `info <id>`, `upload <path>`, `download <id> <dest>`, `delete <id>`,
    `metadata set <id> <name> <content-type> <value> [...]`,
    `functions register <dllPath> <name> [runtime=dotnet]`
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
```
