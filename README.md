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
    `metadata set <id> <name> <content-type> <value> [...]`
- Test API: `dotnet test tests/Clout.Api.IntegrationTests/Clout.Api.IntegrationTests.csproj`

Tip: Set `CLOUT_API` to point the client at a non-default base URL (default `http://localhost:5000`).
