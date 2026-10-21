---
name: add-page
description: Add or change a Blazor page, shared component, or service in DevDb Studio. Use when asked for a new page, menu item, UI feature or service.
---

# Add a page or service

There are no HTTP API endpoints; features are services called from Razor pages.

**Service**
1. Define `I<Name>` and implementation in `Services/` (engine plumbing) or `Services/Dbms/` (DBMS logic).
2. Register in `Program.cs` (singleton unless it holds per-circuit state).
3. Return result objects for expected failures; accept and pass `CancellationToken`; log with message templates; never log secrets.
4. Run engine commands only through `IWslDockerRunner`, with discrete arguments.

**Page**
1. Create `Components/Pages/<Name>.razor` with `@page "/<route>"`; use `@inject` with interfaces.
2. Create a `CancellationTokenSource`, cancel and dispose it in `Dispose`.
3. Add a `NavLink` in `Components/Layout/MainLayout.razor`.
4. Reuse classes from `wwwroot/app.css` (`card`, `btn`, `chip`, `form`, `toolbar`) before adding CSS; support light and dark themes via CSS variables.
5. Browser interop goes through `window.devDbStudio` in `wwwroot/app.js`.
6. Razor quirks: write `v@(Name)` not `v@Name`; a literal `@` is `@@`.
7. Document it on `Components/Pages/Help.razor` if user-visible.

**Always:** add tests for logic (extract it from the page into a service), then follow the `release` and `verify` skills.
