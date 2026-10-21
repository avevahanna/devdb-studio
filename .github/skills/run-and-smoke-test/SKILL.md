---
name: run-and-smoke-test
description: Start DevDb Studio locally and smoke-test pages with curl. Use to check UI or startup changes actually work at runtime.
---

# Run and smoke-test

Use a throwaway port and data file so real instances are untouched.

```powershell
$env:ASPNETCORE_URLS = "http://127.0.0.1:5099"
$env:Storage__InstancesFile = "$env:TEMP\dbs-smoke\instances.json"
dotnet run --project DevDbStudio.csproj --no-launch-profile
```

Run it as an async/detached process, wait until it listens, then:

```powershell
curl.exe -s -o NUL -w "%{http_code}" http://127.0.0.1:5099/
curl.exe -s -o NUL -w "%{http_code}" http://127.0.0.1:5099/help
curl.exe -s -o NUL -w "%{http_code}" http://127.0.0.1:5099/instances/create
```

- Expect 200 for each route; check the HTML contains "DevDb Studio".
- Stop it with `Stop-Process -Id <PID>` using the specific PID (never by name), and delete `$env:TEMP\dbs-smoke`.
- Default port is 5190; do not stop an app the user already runs there.
- Interactive Blazor behaviour (buttons, editors) cannot be checked with curl; say so when reporting.
