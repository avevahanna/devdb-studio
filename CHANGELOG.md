# Changelog

Versioning follows [SemVer](https://semver.org): `MAJOR.MINOR.PATCH`.

- **PATCH** (1.0.1): bug fixes only.
- **MINOR** (1.1.0): new features, backwards compatible.
- **MAJOR** (2.0.0): breaking changes (e.g. settings or data file format).

The version lives in one place: `<Version>` in `DbServeApp.csproj`. Bump it with every change and add an entry here.
It is shown in the sidebar footer.

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
