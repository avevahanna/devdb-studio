# Prompt template – bug fix

Context: read `AI.md` and `.github/copilot-instructions.md` first.

## 1. Reproduce
- Symptom (exact error text or screenshot description):
- Page/route and DBMS/engine/distro involved:
- Steps to reproduce:
- Expected vs actual:
- Version (`<Version>` in `DbServeApp.csproj`, shown in the sidebar footer) and relevant log lines (never include passwords).
Reproduce with the smallest possible case, preferably as a failing unit test in `tests/DbServeApp.Tests/` (fake `IWslDockerRunner` or feed captured output to the parser).

## 2. Root cause
State the cause in one or two sentences with file and line (for example "`Query.razor` prefixes `USE` for MongoDB"). Check whether the same defect exists in the other providers (`Services/Dbms/*Provider.cs`) or dialects (`TableDialect.cs`).

## 3. Fix
- Smallest change that removes the cause; no unrelated refactoring.
- Keep invariants from `AI.md` (loopback binding, validated input, stable names, backward compatible JSON).
- Return a clear result/error message instead of throwing for expected failures.

## 4. Test
- The reproducing test now passes; add edge cases next to it.
- `dotnet test tests\DbServeApp.Tests` and `dotnet build DbServeApp.csproj -warnaserror` pass.

## 5. Finish
Patch version bump in `DbServeApp.csproj`, `CHANGELOG.md` entry ("Fixed: …"), README only if behaviour documented there changed. Summarise cause, fix and verification.
