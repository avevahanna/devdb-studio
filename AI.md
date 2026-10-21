# AI.md – project context

## Overview
DB Serve is a local web app (Blazor Server, .NET 10, default <http://localhost:5190>) for creating and managing development database servers without Docker Desktop. Each "instance" is a Docker or Podman container running inside a WSL2 Linux distro; the app calls `wsl.exe -d <distro> --exec <docker|podman> …` and never talks to a daemon API directly.

On top of instance lifecycle it offers a Query page (run SQL or mongosh in the container), a table designer and random-data tool for the SQL databases, a Status page (containers, images, volumes, networks, host metrics, database insights) and a Setup page that checks prerequisites. The WSL distro and the container engine are auto-detected.

## Key modules
| Module | Path | Purpose |
|--------|------|---------|
| Providers | `Services/Dbms/*Provider.cs` | Create/start/stop/delete a DBMS container and run queries (`IDbmsProvider`) |
| Instance manager | `Services/Dbms/InstanceManager.cs` | Orchestrates providers, repository and engine status |
| Repository | `Services/Dbms/InstanceRepository.cs` | JSON file store of instances (atomic save with retry) |
| Validation/models | `Services/Dbms/Models.cs` | `DbInstance`, requests/results, `InstanceValidation` |
| Table tools | `TableDialect.cs`, `TableDataService.cs`, `*DataScript.cs`, `MssqlTableScript.cs` | DDL generation, populate/clear data per dialect |
| Output parsing | `SqlcmdOutputParser.cs`, `MySqlOutputConverter.cs`, `PsqlOutputConverter.cs` | Turn client output into result tables |
| Insights | `Services/Dbms/DbInsightService.cs`, `MongoScripts.cs` | Connections, databases, size per DBMS |
| Engine/WSL | `Services/ContainerEngine.cs`, `WslDockerRunner.cs`, `WslKeepAliveService.cs`, `ProcessRunner.cs` | Distro + engine detection, command execution, keep WSL alive |
| Docker service | `Services/DockerService.cs`, `DockerService.Details.cs` | Cached snapshot for the Status page |
| Prerequisites | `Services/PrerequisiteCheckService.cs` | Setup page checks and fix instructions |
| UI | `Components/Pages`, `Components/Shared`, `Components/Layout`, `wwwroot` | Pages, shared components, AVEVA-styled CSS/JS |
| Tests | `tests/DbServeApp.Tests` | xUnit, no WSL required |

## Invariants – must never break
1. Containers publish ports on `127.0.0.1` only (`-p 127.0.0.1:<port>:<internal>`).
2. The app serves loopback clients only unless `Security:AllowRemoteAccess` is true; the Origin check stays.
3. Engine commands go through `IWslDockerRunner`/`ProcessRunner` with discrete arguments; never build a shell command from unvalidated input.
4. Secrets travel as environment variables, scripts via stdin; never as command-line arguments that are logged.
5. Container names `dbapp-<key>-<shortId>`, volume `<container>-data` and labels `dbapp.managed/id/dbms` stay stable.
6. `instances.json` and `settings.json` stay backward compatible (optional new fields only).
7. Instance data is only deleted when the user explicitly ticks "delete volume".
8. Create failures clean up the container and volume they started.
9. The app never touches containers it did not create unless the user acts on them in the Status page.
10. `<Version>` in `DbServeApp.csproj` is the single source of the version; `CHANGELOG.md` is updated with every change.
11. Tests must run without WSL, Docker or network.

## Common workflows
**Add a page:** create `Components/Pages/<Name>.razor` with `@page "/<route>"`, inject interfaces, use a `CancellationTokenSource` disposed in `Dispose`; add a `NavLink` in `Components/Layout/MainLayout.razor`; reuse CSS classes from `wwwroot/app.css`.

**Add a service ("endpoint"):** there are no HTTP API endpoints; features are services called from Razor pages. Define `I<Name>` + implementation in `Services/` (or `Services/Dbms/`), register in `Program.cs`, return result objects, add tests.

**Add a DBMS:** follow the checklist in `.github/copilot-instructions.md` ("Adding a DBMS"); `MongoDbProvider.cs` is the compact example, `MySqlFamilyProvider.cs` the SQL one.

**Add a feature:** use `prompts/feature.md`; finish with tests, `CHANGELOG.md`, version bump and README.
