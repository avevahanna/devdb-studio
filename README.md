# DB Serve

A local web app for creating and managing database servers that run as **Docker or Podman** containers inside **Ubuntu on WSL2** — no Docker Desktop required.

Built with .NET 10 Blazor Server (interactive server rendering), running on Windows.

## Features

- **Setup** (`/setup`) – prerequisite checker for WSL, the Ubuntu distro, the container engine (Docker or Podman: CLI, daemon/`info`, Compose). Failed checks show why it matters and exact fix commands. The **Container engine** selector switches between auto-detect (a working Docker is preferred, then Podman), Docker and Podman; the choice is saved in `settings.json` next to the instances file.
- **Instances** (`/instances`, `/instances/create`) – create, start, stop and delete database instances. Cards show the DBMS, status, port, and a connection string with reveal and copy-to-clipboard.
- **Query** (`/query`) – run SQL against a running instance and view results as tables (with a raw-output toggle).
- **Tables** (`/tables`) – visual table designer that generates a T-SQL `CREATE TABLE` script and runs it.
- **Table data** (`/data`) – pick a table and either fill it with 1–10,000 random rows or clear all of its data (with confirmation). SQL Server only.
  - Values are chosen from each column's name and type (e.g. `email`, `city`, `price`, `birthDate`) and respect length and precision. Identity and computed columns are skipped.
  - Foreign keys, including composite ones, point at real rows in the referenced table; if it is empty you are asked to populate it first.
  - Unique indexes and primary keys are honoured: unique columns continue above the existing maximum, and foreign-key-only unique indexes (junction tables) get distinct combinations. If uniqueness can't be guaranteed you get a clear error.
  - Inserts run in a single transaction, so a failure leaves the table unchanged. `CHECK` constraints are not known to the generator and can still cause a failure.
  - Clearing uses `TRUNCATE`, or `DELETE` plus an identity reseed when other tables reference the table (it fails if rows there still point at the table).
- **Status** (`/status`) – live container engine statistics (CPU/memory, containers, images, volumes, networks, disk usage) with auto-refresh, plus container administration (start, stop, restart, pause, kill, remove, logs, inspect).

### Supported DBMS

| DBMS | Status |
|------|--------|
| Microsoft SQL Server | Implemented |
| PostgreSQL | Stub (TODO) |
| MariaDB | Stub (TODO) |

New DBMS support is added by implementing `IDbmsProvider` (`Services/Dbms`) and registering it in `Program.cs`.

## Prerequisites

- Windows with WSL2 and an Ubuntu distro
- Docker Engine + Compose plugin, **or** Podman, installed **inside** Ubuntu
- .NET 10 SDK

The Setup page verifies these and explains how to fix anything missing.

## Run

```powershell
dotnet run
```

Then open <http://localhost:5190>.

## Configuration (`appsettings.json`)

| Key | Default | Description |
|-----|---------|-------------|
| `Wsl:DistroName` | `Ubuntu` | WSL distro that hosts Docker |
| `Wsl:CommandTimeoutSeconds` | `10` | Default timeout for `wsl.exe` commands |
| `Wsl:ColdStartTimeoutSeconds` | `60` | Timeout when WSL is starting up |
| `Wsl:KeepAlive` | `true` | Keeps a `wsl.exe` session open so WSL doesn't shut down and stop the containers while the app runs |
| `Wsl:ContainerEngine` | `Auto` | `Auto`, `Docker` or `Podman`. A choice saved from the Setup page overrides this |
| `Storage:InstancesFile` | `%LocalAppData%\DbServeApp\instances.json` | Where instances are persisted |
| `Security:AllowRemoteAccess` | `false` | Allow non-loopback clients (not recommended) |

## Architecture

- `Services/ProcessRunner.cs`, `WslDockerRunner.cs`, `ContainerEngine.cs` – run `wsl.exe -d <distro> --exec <docker|podman> ...` with timeout, cancellation and captured stdout/stderr (no shell quoting). `ContainerEngine` detects/stores which engine to use.
- `Services/Dbms/` – provider pattern: `IDbmsProvider`, `MssqlProvider`, `InstanceManager` (orchestration, status via `docker ps -a`), `JsonInstanceRepository`, `SqlcmdOutputParser`, `MssqlTableScript`, `MssqlDataScript` + `TableDataService` (random population and clearing).
- `Services/DockerService.cs`, `PrerequisiteCheckService.cs` – Status page data and Setup checks.
- `Components/Pages`, `Components/Shared` – UI. Styling in `wwwroot/app.css`.

SQL Server containers are created with a named volume at `/var/opt/mssql`, named `dbapp-mssql-<shortId>`, and published on `127.0.0.1:<port>` only. Queries run through `sqlcmd` inside the container. Containers belong to the engine that created them: after switching between Docker and Podman, instances created with the other engine show as not found until you switch back.

## Security notes

- The app only accepts loopback requests and rejects cross-origin requests by default.
- SA passwords are stored in **plain text** in `instances.json` (v1) and are visible in `docker inspect`. Use this for local development only.
- The Query and Tables pages run arbitrary SQL by design. The Table data page can delete all rows of a table.
- Connect from Windows with `127.0.0.1,<port>` (not `localhost`, which may resolve to IPv6).

## Out of scope

Authentication, multi-user support, automatic installation of WSL/Docker, and advanced result grids.

## Author

Mattias Dahlgren — [mattias.dahlgren@aveva.com](mailto:mattias.dahlgren@aveva.com)
