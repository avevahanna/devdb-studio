# Prompt template – refactor

Context: read `AI.md`, `ARCHITECTURE.md` and `.github/copilot-instructions.md` first.

## 1. Goal
- What gets better (duplication removed, clearer boundary, smaller file, testability):
- Scope (files/folders): for example `Services/Dbms/`, `Components/Pages/Status.razor`.
- Non-goals: no behaviour change, no new features.

## 2. Constraints
- Public behaviour, UI output, `instances.json`/`settings.json` formats, container/volume/label names and the invariants in `AI.md` stay identical.
- Keep DI via interfaces; registrations stay in `Program.cs`.
- Do not reformat unrelated files.
- Existing tests must pass unchanged; if a test must change, explain why.

## 3. Steps
1. Add characterization tests in `tests/DbServeApp.Tests/` for code that lacks coverage (parsers, scripts, providers via fake runner).
2. Refactor in small steps; build after each (`dotnet build DbServeApp.csproj -warnaserror`).
3. Move or rename with the IDE or `dotnet format`-safe edits; update every reference, including Razor files and tests.
4. Remove dead code only when nothing references it.

## 4. Safety checks
- `dotnet test tests\DbServeApp.Tests` green before and after.
- `dotnet format DbServeApp.csproj --verify-no-changes --include <changed files>` clean.
- Grep for old names (`Services`, `Components`, `tests`, `README.md`) to catch stale references.
- Manually smoke-test affected pages if UI changed (`dotnet run`, http://localhost:5190).

## 5. Finish
Patch version bump and `CHANGELOG.md` entry ("Internal: …"). Summarise what moved and confirm that behaviour is unchanged.
