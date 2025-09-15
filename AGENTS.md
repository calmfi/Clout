# Repository Guidelines

## Project Structure & Module Organization
- API: Project `Clout.Host` (path: `Clout.Host/Clout.Host.csproj`) - ASP.NET Core Minimal API exposing local blob storage with Swagger.
- Client: `Clout.Client/` - Console client for the API (upload, list, info, download, delete).
- Shared: `Cloud.Shared/` - Contracts and abstractions shared across projects (e.g., `BlobInfo`, `IBlobStorage`).
- Build artifacts: `**/bin/`, `**/obj/` (ignored). IDE cache: `.vs/` (ignored).
- Solutions: lightweight `Clout.slnx` only. Do not use or add `.sln` files.

## Build, Test, and Development Commands
- `dotnet restore` — restore NuGet packages.
- `dotnet build` — build all projects.
- `dotnet run --project Clout.Host/Clout.Host.csproj` — start API at `http://localhost:5000` with Swagger UI.
- `dotnet run --project Clout.Client -- <cmd>` - run client (`list|info|upload|download|delete`).
- `dotnet format` — apply code style/lint fixes.

## Coding Style & Naming Conventions
- C# with nullable enabled; 4-space indentation; file-scoped namespaces.
- Naming: PascalCase (types/members), camelCase (locals/params), `_camelCase` (private fields).
- One public type per file; filename matches type (e.g., `BlobApiClient.cs`).
- Prefer `var` when the type is obvious; avoid abbreviations.

## Shared Contracts
- Place cross-project interfaces and models in `Cloud.Shared/` (e.g., `BlobInfo`, `IBlobStorage`).
- Reference `Cloud.Shared` from any project that needs these types; do not duplicate contracts locally.
- When changing a shared type, ensure all consumers compile and behavior is documented. Update XML docs and examples if shape changes.
- Consider backward compatibility: add fields as optional when feasible, avoid breaking renames without coordinating client/API changes.

## Cancellation & Async
- All async APIs accept `CancellationToken` with a default parameter.
- Minimal API endpoints receive `CancellationToken ct` (bound from `RequestAborted`) and pass it through.
- Propagate tokens to I/O: `CopyToAsync(stream, ct)`, `ReadFormAsync(ct)`, `SerializeAsync(..., ct)`, `DeserializeAsync(..., ct)`.
- For loops over files, call `ct.ThrowIfCancellationRequested()` per iteration.
- Client wires Ctrl+C to a single `CancellationTokenSource` and passes the token to every call.

## Testing Guidelines
- Framework: xUnit (add `tests/Clout.Tests/` when tests are introduced).
- Structure tests to mirror production namespaces; file names `TypeNameTests.cs`.
- Test names: `Method_WhenCondition_ExpectedResult`. Run via `dotnet test`.

## Commit & Pull Request Guidelines
- Use Conventional Commits: `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`.
- Keep commits focused and descriptive; include rationale in bodies when useful.
- PRs: clear summary, linked issues, reproduction/validation steps, and relevant screenshots (e.g., Swagger UI).

## Security & Configuration Tips
- Never commit secrets. Use environment variables or user-secrets for local config.
- Client base URL via `CLOUT_API` (default `http://localhost:5000`).
- Storage lives under the API’s `storage/` folder in the runtime output path.

## Agent-Specific Instructions
- Respect this file's scope. Do not add `.sln` files; keep using `Clout.slnx` and `dotnet` CLI.
- Keep changes minimal, focused, and consistent with existing style. Update docs when behavior changes.
