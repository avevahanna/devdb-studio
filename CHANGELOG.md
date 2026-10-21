# Changelog

Versioning follows [SemVer](https://semver.org): `MAJOR.MINOR.PATCH`.

- **PATCH** (1.0.1): bug fixes only.
- **MINOR** (1.1.0): new features, backwards compatible.
- **MAJOR** (2.0.0): breaking changes (e.g. settings or data file format).

The version lives in one place: `<Version>` in `DbServeApp.csproj`. Bump it with every change and add an entry here.
It is shown in the sidebar footer.

## 1.5.0

- **Volumes can be deleted** on the Status page (Volumes tab): each volume that no container uses has a Delete button with confirmation. Use it to clean up data volumes that were kept when an instance was deleted.

## 1.4.0

- New **Help** page (`/help`, under a new "Help" section in the sidebar below "System"): step-by-step usage guide, a description of every page, a card per DBMS with its logo, defaults, client and example, connection addresses, data handling and troubleshooting.

## 1.3.1

- DBMS badges use the new SVG logos (replacing the PNG/placeholder icons) in a larger rounded white tile that stays readable in every theme.

## 1.3.0

- Added **TimescaleDB** (`docker.io/timescale/timescaledb`, default tag `latest-pg17`). It shares everything with PostgreSQL (psql, SQL Query page, table designer, table data, Status insights); timescaledb-internal schemas are hidden from table lists.
- Added **Redis** (`docker.io/library/redis`, default tag `8`, password required, append-only persistence). The Query page takes redis-cli commands, one per line (database `0`–`15` selectable); Status insights show clients, keyspaces and memory. No table designer or table data. The create form has no database name field for Redis.
- `PostgresProvider` now derives from a shared `PostgresFamilyProvider`; existing PostgreSQL container names are unchanged. Providers gained `UsesDatabaseName`.

## 1.2.0

- The WSL distro is now **auto-detected** (default `Wsl:DistroName` is `Auto`): the first distro with a working Docker or Podman is used, so Ubuntu, Debian, Fedora, `podman-machine-default` and others work without configuration. Docker Desktop's helper distros are ignored.
- The Setup page has a **WSL distro** selector next to the container engine selector; the choice is saved in `settings.json`. An explicit `Wsl:DistroName` still works.
- Setup checks and install hints no longer assume Ubuntu.
- 7 new tests (138 total).

## 1.1.0

- Added **MongoDB** support (`docker.io/library/mongo`, default tag `8`): create, start, stop and delete; connection string includes the database and `authSource=admin`.
- Query page runs mongosh JavaScript for MongoDB instances and shows the output as plain text; database list and Status page insights (connections, databases, size) work for MongoDB.
- Table designer and Table data skip MongoDB instances; providers gained `SupportsTables`, `QueryLanguage` and `SampleQuery`.
- The MongoDB sample query now also shows commented insert and find examples.
- 8 new tests (131 total).

## 1.0.1

- Added an automated xUnit test suite (`tests\DbServeApp.Tests`, 123 tests).
- Fixed: saving the instance list could occasionally fail on Windows when the file was briefly locked; the save is now retried.

## 1.0.0

First versioned release: SQL Server, PostgreSQL, MariaDB and MySQL on Docker or Podman in WSL2; query, table designer and
table data tools; AVEVA-styled UI with light/dark theme; extended Status page with container details, database insights,
host resources and events.
