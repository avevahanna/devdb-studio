# Copilot instructions – DevDb Studio

DevDb Studio is a .NET Blazor Server app that creates and manages database servers (SQL Server, PostgreSQL, TimescaleDB, MariaDB, MySQL, MongoDB, Redis) as Docker or Podman containers inside a WSL2 Linux distro. Read `AI.md` and `ARCHITECTURE.md` before larger changes.

## Tech stack
- .NET 10 (`net10.0`), ASP.NET Core Blazor Server (interactive server components), C# with `Nullable` and `ImplicitUsings` enabled. Single project: `DevDbStudio.csproj`.
- No database or ORM of its own: instances are stored in a JSON file (`Services/Dbms/InstanceRepository.cs`); settings in `settings.json`.
- All container/DB work is done by shelling out through `Services/EngineHost.cs` (`wsl.exe` on Windows, `docker`/`podman` directly on Linux) via `Services/ProcessRunner.cs`.
- Tests: xUnit 2.9 on `net10.0` in `tests/DevDbStudio.Tests`. CI: `.github/workflows/ci.yml` (ubuntu-latest).
- Frontend: plain CSS (`wwwroot/app.css`) and small JS helpers (`wwwroot/app.js`, namespace `devDbStudio`). No npm or build step.

## Where code goes
- `Program.cs` – DI registration and middleware only.
- `Services/` – engine and WSL plumbing (`ContainerEngine`, `WslDockerRunner`, `DockerService*`, `PrerequisiteCheckService`, `ProcessRunner`).
- `Services/Dbms/` – everything DBMS related: one `<Name>Provider.cs` per DBMS implementing `IDbmsProvider`, script/parsing helpers (`*DataScript.cs`, `*OutputConverter.cs`, `SqlcmdOutputParser.cs`, `MongoScripts.cs`), `InstanceManager`, `TableDataService`, `DbInsightService`, `Models.cs`.
- `Components/Pages/` – routable pages (`@page`). `Components/Shared/` – reusable components. `Components/Layout/` – layout and navigation.
- `wwwroot/` – static assets; DBMS icons in `wwwroot/icons/`.
- `tests/DevDbStudio.Tests/` – one test file per unit under test (`<Thing>Tests.cs`).
- `.github/skills/<name>/SKILL.md` – reusable task playbooks (`add-dbms`, `add-page`, `release`, `verify`, `run-and-smoke-test`). Use them when the task matches.
- Docs: `README.md` (users), `CHANGELOG.md` (every version), `AI.md`, `ARCHITECTURE.md`, `CONTRIBUTING.md`, `prompts/`.

## Coding conventions
- Namespaces: `DevDbStudio.Services`, `DevDbStudio.Services.Dbms`. File-scoped namespaces. One public type per file, file named after the type.
- Naming: `PascalCase` types/members, `_camelCase` private fields, `IName` for interfaces, `Async` suffix on async methods. Provider keys are lowercase constants (`MongoDbProvider.ProviderKey = "mongodb"`).
- DI: constructor injection (primary constructors are used). Register in `Program.cs`; services are singletons unless they hold per-circuit state. Razor pages use `@inject`. Depend on interfaces (`IInstanceManager`, `IWslDockerRunner`, …), never on concrete providers.
- Async: `async`/`await` end to end, accept and pass a `CancellationToken ct`, no `.Result`/`.Wait()`. Pages create a `CancellationTokenSource` and cancel it in `Dispose`.
- Logging: `ILogger<T>` with message templates (`log.LogInformation("Created {Dbms} instance {Name}", …)`), never string interpolation. Never log passwords or connection strings with passwords.
- Errors: expected failures are returned as result objects (`InstanceCreateResult`, `InstanceActionResult`, `QueryResult`), not thrown. Catch only specific exceptions; let `OperationCanceledException` propagate. User-facing messages must say what to do next.
- Nullability: no `!` unless a prior check guarantees non-null; prefer `is { } x` patterns. Models are `record`s; update with `with`.
- Providers build the container command in `BuildRunArgs` (also used by `ComposePreview`) and delegate start/stop/delete/cleanup to `ContainerLifecycle`; do not duplicate lifecycle code. Quote passwords in connection strings with `ConnectionStrings.Value()`. Secrets go through `ContainerEnvironment`.
- Adding a DBMS (see the `add-dbms` skill): implement `IDbmsProvider` (copy `MongoDbProvider` or `MySqlFamilyProvider`), register it in `Program.cs`, add the icon and `DbmsBadge.razor` mapping, and handle it in `TableDialect`, `TableDataService`, `DbInsightService` and `Query.razor` as applicable. Use `SupportsTables`/`QueryLanguage` for non-SQL providers.
- Run container engine commands only through `IWslDockerRunner` (arguments are discrete tokens, no shell). Pass secrets via `-e` env vars and scripts via stdin, as the existing providers do.
- Razor: `v@Name` is parsed as an e-mail address; write `v@(Name)`.
- UI follows the AVEVA look (CSS variables in `wwwroot/app.css`, light/dark themes). Reuse existing classes (`card`, `btn`, `chip`, `form`, `toolbar`) before adding new CSS.

## Testing rules
- Framework: xUnit. Tests live in `tests/DevDbStudio.Tests/` and must not need WSL, Docker or network. Fake `IWslDockerRunner` / `IInstanceManager` (see `MongoDbTests.cs`, `DbInsightServiceTests.cs`).
- Test pure logic: validation, SQL/script generation, output parsing, JSON parsing, providers via a fake runner. Parsers in `DockerService` are `internal` and exposed via `InternalsVisibleTo`.
- Every behaviour change or bug fix gets a test. Name tests `Method_Condition_Expected` or a descriptive sentence.
- Run: `dotnet test tests\DevDbStudio.Tests`. If the app is running (it locks `bin\Debug`), add `-o "$env:TEMP\dbs-test-out"`.

## Data / migrations
- There is no SQL schema or migration system. `instances.json` (`Storage:InstancesFile`, default `%LOCALAPPDATA%\DbServeApp\instances.json`) and `settings.json` must stay backward compatible: only add optional fields with defaults, never rename or remove existing ones.
- The product was renamed from "DB Serve" to "DevDb Studio" in 1.7.0. The data folder `DbServeApp`, the `dbapp-*` names/labels and `dbserve-*` localStorage keys intentionally keep the old names so existing installs keep working.
- Container and volume names (`dbapp-<key>-<shortId>`, `…-data`) and labels (`dbapp.managed`, `dbapp.id`, `dbapp.dbms`) are relied upon by existing installs; do not change them.

## Security rules
- No secrets in source, tests, docs or logs. Test passwords are obviously fake.
- Validate all user input at the boundary (`InstanceValidation` in `Services/Dbms/Models.cs`): names, ports 1024–65535, tags, database name `^[A-Za-z][A-Za-z0-9_]{0,63}\z`, password rules. Anything interpolated into a script or `sh -c` string must be validated or escaped first (see `SafeId` in `DockerService.cs`).
- Containers bind to `127.0.0.1` only. The app has no authentication, so it serves loopback clients only unless `Security:AllowRemoteAccess` is set; keep the loopback and Origin checks in `Program.cs`.
- Passwords are stored in plain text in `instances.json` by design (local dev tool); never display them unmasked except through `ConnectionStringBox` reveal.

## Definition of Done
- [ ] `dotnet build DevDbStudio.csproj -warnaserror` succeeds with 0 warnings.
- [ ] `dotnet test tests\DevDbStudio.Tests` passes; new behaviour has tests.
- [ ] Changed files are formatted: `dotnet format DevDbStudio.csproj --include <changed files>` (the repo has older, unformatted files; do not reformat unrelated code).
- [ ] `<Version>` in `DevDbStudio.csproj` bumped (SemVer: fix = patch, feature = minor, breaking = major) and an entry added to `CHANGELOG.md`.
- [ ] `README.md` updated if user-visible behaviour, configuration or prerequisites changed.
- [ ] No secrets, no leftover debug code, no unrelated edits.
