---
name: add-dbms
description: Add support for a new database system (DBMS) to DevDb Studio as a container provider. Use when asked to add, implement or support another database such as Cassandra, Neo4j or CockroachDB.
---

# Add a DBMS

Reference implementations: `Services/Dbms/MongoDbProvider.cs` (non-SQL, compact), `MySqlFamilyProvider.cs` (SQL), `RedisProvider.cs` (key/value).

1. **Provider** – create `Services/Dbms/<Name>Provider.cs` implementing `IDbmsProvider`:
   - `ProviderKey` (lowercase constant), display name, default port/tag, `QueryLanguage`, `SupportsTables`.
   - `PasswordEnvVar` / `DatabaseEnvVar` and `BuildRunArgs(req, id, container, volume)`. `CreateAsync` and `ComposePreview` both use `BuildRunArgs`; ports bind `127.0.0.1:<port>:<internal>` only.
   - Start/Stop/Delete/cleanup: delegate to `ContainerLifecycle`; never copy lifecycle code.
   - Readiness: wait for a real log marker or ping before reporting success; verify the marker against a real container if you can (and remove it afterwards).
   - Connection string: use `ConnectionStrings.Value()` for passwords. Include the database name where the DBMS has one.
   - Secrets via `ContainerEnvironment` (env), scripts via stdin, never argv.
2. **Register** in `Program.cs`.
3. **UI** – add `wwwroot/icons/<key>.svg`, map it in `Components/Shared/DbmsBadge.razor`, add a section to `Components/Pages/Help.razor`, and handle it in `Query.razor` where applicable.
4. **Dialects** – for SQL-like DBMSs extend `TableDialect`, `TableDataService`, a `*DataScript.cs` and `DbInsightService`; for others set `SupportsTables = false`.
5. **Validation** – check `InstanceValidation` in `Models.cs` still fits (name, port, tag, database name, password rules).
6. **Tests** – add `tests/DevDbStudio.Tests/<Name>Tests.cs` using a fake `IWslDockerRunner` (see `MongoDbTests.cs`): create args, failure cleanup, connection string, `ComposePreview` round trip.
7. **Docs/release** – README feature list, Help page, ARCHITECTURE/AI.md if modules changed, then follow the `release` skill (minor bump).
8. Run the `verify` skill.
