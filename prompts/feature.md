# Prompt template – new feature

Context: read `AI.md`, `ARCHITECTURE.md` and `.github/copilot-instructions.md` first.

## 1. Requirements
- Goal (one sentence):
- User-visible behaviour (page, route, button, setting):
- Affected DBMSs (mssql, postgres, mariadb, mysql, mongodb) and engines (Docker, Podman):
- Out of scope:
- Acceptance criteria (testable bullets):

## 2. Implementation plan
Produce a short numbered plan before editing. For each step name the layer:
1. Model/validation – `Services/Dbms/Models.cs` (backward compatible JSON only).
2. Service/provider logic – `Services/` or `Services/Dbms/`; new interface registered in `Program.cs`.
3. UI – `Components/Pages/*.razor`, `Components/Shared/*.razor`, nav in `Components/Layout/MainLayout.razor`, styles in `wwwroot/app.css`.
4. Docs/version – `README.md`, `CHANGELOG.md`, `<Version>` in `DbServeApp.csproj` (minor bump).

## 3. Files to touch
List every file to create or change, with one line on why. Reuse existing patterns (`MongoDbProvider.cs`, `DbInsightService.cs`, `Query.razor`) rather than inventing new ones.

## 4. Tests
- New/changed tests in `tests/DbServeApp.Tests/<Thing>Tests.cs` using fake `IWslDockerRunner`/`IInstanceManager`.
- Cases: happy path, invalid input, command failure/timeout, each affected DBMS.
- Run `dotnet test tests\DbServeApp.Tests`; also `dotnet build DbServeApp.csproj -warnaserror`.

## 5. Finish
Walk through the Definition of Done in `.github/copilot-instructions.md`. Summarise what was verified and what was not (for example, not run against a real container engine).
