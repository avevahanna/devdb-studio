---
name: verify
description: Build, test and format-check DevDb Studio changes before finishing. Use after any code change, before committing or reporting a task done.
---

# Verify

Run from the repo root. Build to a temp folder so a running app (which locks `bin\Debug`) does not block you.

```powershell
dotnet build DevDbStudio.csproj -warnaserror -o "$env:TEMP\dbs-out"
dotnet test tests\DevDbStudio.Tests -o "$env:TEMP\dbs-test-out"
dotnet format DevDbStudio.csproj --verify-no-changes --include <changed files>
Remove-Item -Recurse -Force "$env:TEMP\dbs-out","$env:TEMP\dbs-test-out" -ErrorAction SilentlyContinue
```

- Expect 0 warnings and 0 failures (194+ tests, no WSL/Docker needed).
- Format only the files you changed; there is no `.editorconfig` and older files are unformatted.
- If build or tests fail because of locked files, a VS Code C# Dev Kit or the running app holds them. Do not kill those processes; keep using `-o` temp folders.
- Do not touch real containers (`dbapp-*`). Remove any throwaway containers you start.
- Do not commit unless asked; commits need the trailer `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.
