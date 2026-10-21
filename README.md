# DB Serve

A local web app for creating and managing database servers that run as **Docker or Podman** containers inside **Ubuntu on WSL2** — no Docker Desktop required.

Built with .NET 10 Blazor Server (interactive server rendering), running on Windows.

## Features

- **Setup** (`/setup`) – prerequisite checker for WSL, the Ubuntu distro, the container engine (Docker or Podman: CLI, daemon/`info`, Compose). Failed checks show why it matters and exact fix commands. The **Container engine** selector switches between auto-detect (a working Docker is preferred, then Podman), Docker and Podman; the choice is saved in `settings.json` next to the instances file.
- **Instances** (`/instances`, `/instances/create`) – create, start, stop and delete database instances. Cards show the DBMS, status, port, and a connection string with reveal and copy-to-clipboard.
- **Query** (`/query`) – run SQL against a running instance and view results as tables (with a raw-output toggle). Uses `sqlcmd` for SQL Server, `psql` for PostgreSQL, `mariadb` for MariaDB and `mysql` for MySQL. Run with **Ctrl+Enter**, pick the database from a list of the instance's databases, re-use the last 20 queries per instance (history) or named snippets (both stored in the browser's local storage), and download any result table as **CSV** or **JSON**.
- **Tables** (`/tables`) – visual table designer that generates a `CREATE TABLE` script (T-SQL, PostgreSQL, MariaDB or MySQL, depending on the instance) and runs it.
- **Table data** (`/data`) – pick a table and either fill it with 1–10,000 random rows or clear all of its data (with confirmation). Works for SQL Server, PostgreSQL, MariaDB and MySQL.
  - Values are chosen from each column's name and type (e.g. `email`, `city`, `price`, `birthDate`) and respect length and precision. Identity/serial, generated and computed columns are skipped.
  - Foreign keys, including composite ones, point at real rows in the referenced table; if it is empty you are asked to populate it first.
  - Unique indexes and primary keys are honoured: unique columns continue above the existing maximum (or get unique suffixes/UUIDs). On SQL Server, foreign-key-only unique indexes (junction tables) get distinct combinations; on PostgreSQL, MariaDB and MySQL such indexes are reported as unsupported. If uniqueness can't be guaranteed you get a clear error.
  - Inserts run in a single transaction, so a failure leaves the table unchanged. `CHECK` constraints are not known to the generator and can still cause a failure.
  - Clearing uses `TRUNCATE`, or `DELETE` plus an identity/sequence reset when other tables reference the table (it fails if rows there still point at the table).
- **Status** (`/status`) – live container engine statistics (CPU/memory, network and disk throughput, containers, images, volumes, networks, disk usage) with auto-refresh, plus container administration (start, stop, restart, pause, kill, remove, logs, inspect). It also shows per-container details (uptime, restarts, OOM, health, mounts, IPs), live database insights per instance (connections, databases, data size), WSL host resources (load, memory, swap, disk), recent container events, and which containers use each image, volume and network. Podman data sources are best-effort.
- **Light/dark theme** – toggle in the top bar; defaults to your OS preference and remembers your choice. DBMS icons are served from `wwwroot/icons`. The sidebar layout, palette (blue `#3051db`, purple `#6b04a8`) and fonts (Mulish, Barlow) follow the AVEVA look.
- **Getting started** – `/` opens the Instances page, which shows a four-step checklist (prerequisites → instance → table → data) until the first instance exists.

### Supported DBMS

| DBMS | Status |
|------|--------|
| Microsoft SQL Server | Implemented |
| PostgreSQL | Implemented |
| MariaDB | Implemented |
| MySQL | Implemented |

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
- `Services/Dbms/` – provider pattern: `IDbmsProvider`, `MssqlProvider`, `PostgresProvider`, `MariaDbProvider` and `MySqlProvider` (both on the shared `MySqlFamilyProvider` base), `InstanceManager` (orchestration, status via `docker ps -a`), `JsonInstanceRepository`, `SqlcmdOutputParser` (`PsqlOutputConverter` and `MySqlOutputConverter` rewrite psql/mariadb output into the same shape), `TableDialect` + `MssqlTableScript` (designer scripts), `MssqlDataScript`/`PostgresDataScript`/`MariaDbDataScript` + `TableDataService` (random population and clearing).
- `Services/DockerService.cs`, `PrerequisiteCheckService.cs` – Status page data and Setup checks.
- `Components/Pages`, `Components/Shared` – UI. Styling in `wwwroot/app.css`.

SQL Server containers are created with a named volume at `/var/opt/mssql`, named `dbapp-mssql-<shortId>`, and published on `127.0.0.1:<port>` only. Queries run through `sqlcmd` inside the container. PostgreSQL containers use `docker.io/library/postgres:<tag>` (default `17`), user `postgres`, a named volume (`/var/lib/postgresql/data`, or `/var/lib/postgresql` for 18+) named `dbapp-postgres-<shortId>`, and queries run through `psql` inside the container. MariaDB containers use `docker.io/library/mariadb:<tag>` (default `11`), user `root`, a named volume at `/var/lib/mysql` named `dbapp-mariadb-<shortId>`, and queries run through the `mariadb` client inside the container (a schema in MariaDB is the database itself). MySQL containers use `docker.io/library/mysql:<tag>` (default `8.4`), user `root`, a volume named `dbapp-mysql-<shortId>`, and the `mysql` client; they behave like MariaDB. Containers belong to the engine that created them: after switching between Docker and Podman, instances created with the other engine show as not found until you switch back.

## Security notes

- The app only accepts loopback requests and rejects cross-origin requests by default.
- SA / `postgres` / `root` passwords are stored in **plain text** in `instances.json` (v1) and are visible in `docker inspect`. Use this for local development only.
- The Query and Tables pages run arbitrary SQL by design. The Table data page can delete all rows of a table.
- Connect from Windows with `127.0.0.1,<port>` for SQL Server or `127.0.0.1:<port>` for PostgreSQL, MariaDB and MySQL (not `localhost`, which may resolve to IPv6).

## Out of scope

Authentication, multi-user support, automatic installation of WSL/Docker, and advanced result grids.

## Versioning

Semantic versioning (`MAJOR.MINOR.PATCH`). The version is set in `<Version>` in `DbServeApp.csproj`, shown in the sidebar footer, and each change is logged in [CHANGELOG.md](CHANGELOG.md).

## Author

Mattias Dahlgren — [mattias.dahlgren@aveva.com](mailto:mattias.dahlgren@aveva.com)
