# Contributing

## Dev environment
1. Windows 10/11 with WSL2 and any Linux distro containing Docker Engine or Podman (`wsl --install`, then install the engine inside the distro). The app's Setup page checks this and shows fixes.
2. .NET 10 SDK (`dotnet --version` must print 10.x).
3. Clone and run from the repo root:
   ```powershell
   dotnet run
   ```
   Open <http://localhost:5190>. Watch mode: `dotnet watch`.

The unit tests need neither WSL nor a container engine.

## Commands (run from the repo root)
| Step | Command |
|------|---------|
| Restore + build | `dotnet build DevDbStudio.csproj` |
| Build as lint (warnings are errors) | `dotnet build DevDbStudio.csproj -warnaserror` |
| Format check (changed files) | `dotnet format DevDbStudio.csproj --verify-no-changes --include <file> [<file> …]` |
| Format fix (changed files) | `dotnet format DevDbStudio.csproj --include <file> [<file> …]` |
| Tests | `dotnet test tests\DevDbStudio.Tests` |
| Everything | `dotnet build DevDbStudio.csproj -warnaserror; dotnet format DevDbStudio.csproj --verify-no-changes --include <files>; dotnet test tests\DevDbStudio.Tests` |

Notes:
- The repo is not fully formatted yet, so `dotnet format --verify-no-changes` without `--include` reports pre-existing whitespace issues. Format only files you touch.
- While the app is running it locks `bin\Debug`; build and test to a temp folder instead: `dotnet build -o "$env:TEMP\dbs-out"` and `dotnet test tests\DevDbStudio.Tests -o "$env:TEMP\dbs-test-out"`.
- There is no solution file; do not add one at the repo root (it breaks `dotnet build` with "multiple project files"). Build the project explicitly as above.
- CI runs `dotnet build DevDbStudio.csproj -c Release` and `dotnet test tests/DevDbStudio.Tests -c Release`.

## Branches and pull requests
- Branch from `main`: `feature/<short-name>`, `fix/<short-name>`, `refactor/<short-name>`, `docs/<short-name>`.
- Keep a PR to one concern. Commit messages: imperative, short subject (`Add MongoDB provider`), details in the body.
- Every PR must: pass CI, include tests for new behaviour, bump `<Version>` in `DevDbStudio.csproj` (SemVer), add a `CHANGELOG.md` entry and update `README.md` when behaviour or configuration changes.
- Describe what changed, why, and how you verified it (tests run, manual steps). Say so if a path (e.g. Podman, a new DBMS) was not tested against a real engine.
- Never commit secrets, `instances.json`, `settings.json`, `bin/` or `obj/`.
- Read `.github/copilot-instructions.md` for coding conventions and the Definition of Done; reusable task templates are in `prompts/`.
