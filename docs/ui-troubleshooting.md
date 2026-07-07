# UI troubleshooting — when the Blazor dashboard looks wrong

The dashboard is Blazor Server (interactive render mode) over a `Microsoft.NET.Sdk.Web`-library sibling (`Forge.UI.csproj`) that references the host (`Forge.Core.csproj`). Wrong path traversal or a stale manifest can leave the UI in three common failure modes:

## Symptom: full-page reload is needed to see new data

**Cause.** Browser cached `app.css` from a prior build. StaticFileMiddleware ships `app.css` with the .NET default `Cache-Control: public, max-age=...` (24 h, depending on deploy).

**Fix.** Confirm the static-file handler emits `Cache-Control: no-cache` in Development:

```csharp
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uiWwwroot),
    OnPrepareResponse = ctx =>
    {
        if (ctx.Context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment())
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    }
});
```

The page reload-or hard-refresh also works without code changes; the server only sets the header as a hint.

## Symptom: heartbeat stays "unknown" forever

**Cause.** Typed `HttpClient<T>()` registrations had no `BaseAddress`. Server-side relative URL `GET /api/health/heartbeat` threw `InvalidOperationException` ("An invalid request URI was provided"), caught silently by the `AppShellClient` effect.

**Fix.** Always pass the bound URI through `AddForgeUI`:

```csharp
builder.Services.AddForgeUI(new Uri($"http://{options.Hostname}:{options.Port}/"));
```

```csharp
services.AddHttpClient<AppShellClient>(c => c.BaseAddress = baseAddress);
```

## Symptom: dispatch actions never cause re-renders

**Cause.** Fluxor's `<StoreInitializer />` was missing from the render tree. Without it the store never calls `InitializeAsync`, so every `IDispatcher.Dispatch(action)` is a no-op until the first render is rerun.

**Fix.** Add the component to `Routes.razor`:

```razor
<Fluxor.Blazor.Web.StoreInitializer />
<Router AppAssembly="@typeof(Routes).Assembly">...</Router>
```

It belongs inside the interactive render boundary (`<Routes @rendermode="InteractiveServer">`), not in the static HTML doc (`App.razor`).

## Symptom: "index out of range" / "key not in dictionary" on a button click

**Cause.** Two-way binding `@bind="_dict[item.Id]"` compiles to a getter that throws if the key is missing. New rows haven't been added to the dictionary on the first render.

**Fix.** Replace `@bind` with explicit `value` / `@oninput`:

```razor
<input value="@GetText(id)" @oninput="@(e => SetText(id, e.Value?.ToString()))" />

@code {
    string GetText(string id) => _dict.TryGetValue(id, out var v) ? v : "";
    void SetText(string id, string value) => _dict[id] = value;
}
```

## Symptom: 500 on `GET /app.css` with `System.IO.FileNotFoundException: Could not find file '...\Forge\wwwroot\app.css'`

**Cause.** `MapStaticAssets()` was used to serve assets, but its dev-mode runtime patches content using the executing app's `IWebHostEnvironment.ContentRootPath` — which is the *Core* root, not the *UI* project's `wwwroot`. Even though the runtime manifest correctly lists `Forge.UI\wwwroot\` as a content root, the dev override ignores it and resolves relative paths against the wrong directory.

**Fix.** Drop `MapStaticAssets` in favor of `UseStaticFiles` with a `PhysicalFileProvider` pointed directly at the UI wwwroot:

```csharp
var uiWwwroot = Path.GetFullPath(Path.Combine(
    Path.GetDirectoryName(typeof(App).Assembly.Location)!,
    "..", "..", "..", "Forge.UI", "wwwroot"));
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(uiWwwroot) });
```

The path is computed from the UI assembly location at runtime, so the same code works in `dotnet run` and any single-host or containerized deploy.

## Symptom: Stale assembly load after adding a new endpoint, "type X not found in assembly Y"

**Cause.** A background `Forge.Core.exe` from an earlier session is holding a lock on the `bin/Debug/net10.0/Forge.Core.dll`/`Forge.UI.dll`, so the rebuild silently used the cached previous binary.

**Fix.** Use the background_process tool — start the server through a tracked PID; `stop` it before rebuilding; the same PID can be restarted after.

## Symptom: 404 on `/_framework/blazor.web.js` after the Sdk.Web split

**Cause.** When `Sdk.Web` is configured as a `Library` and is built transitively from a non-Web consumer (Core), the Blazor framework's static assets aren't merged into the executing app's static-web-assets manifest. `MapStaticAssets` finds UI assets but not the framework ones.

**Fix.** Copy the framework JS into `Forge.UI/wwwroot/_framework/blazor.web.js` and `<script src="_framework/blazor.web.js">` from `App.razor` will be served by `UseStaticFiles`. The file is `200KB` and identical across `dotnet` versions, so this is a one-time vendor step.

## Symptom: HTTP 500 on `/_content/Fluxor.Blazor.Web.ReduxDevTools/reduxDevTools.js`

**Cause.** The Redux DevTools browser extension script isn't bundled in the Fluxor package — it's served from a separate, optional CDN. `AddFluxor(...).UseReduxDevTools()` only wires *receiving* browser devtools, not the client script.

**Fix.** Either remove the `<script src="_content/Fluxor.Blazor.Web.ReduxDevTools/reduxDevTools.js">` reference in `App.razor`, or pull the script from a CDN. The 404 is harmless (development-only, extension is optional).

## Symptom: tests pass but the browser shows unstyled content

**Cause.** Browser is caching `app.css`. Always true after the first load.

**Fix.** Hard reload (`Ctrl+Shift+R`) or disable cache in DevTools. The `Cache-Control: no-cache` header set in `Development` (see first entry) prevents this across rebuilds.

## Symptom: 500s on every endpoint after staging a stale build

**Cause.** Some `bin/Debug/net10.0/` directories are mounting old assemblies. `dotnet build` cannot copy a new DLL over one that is open in another process.

**Fix.** Kill all background `dotnet`/`MSBuild`/`VBCS`/`Forge.Core` processes before building. Use `Get-Process | Stop-Process -Force` if a build silently no-ops.

## Symptom: a new page or API endpoint returns 404 even though the code is there

**Cause.** `DashboardHost` was constructed without the dependencies the new
endpoint requires (e.g. `ProjectContextFactory` + `SlotTable` for
`/api/projects`). The endpoint is registered only when those dependencies
are non-null. The legacy `--dashboard-only` and the long-running
`RunOrchestratorAsync` both must pass them — if one forgets, that mode
silently hides the endpoint.

**Fix.** Make sure every call site of `new DashboardHost(...)` passes
`projectFactory:` and `slots:` if it uses any API under `/api/projects/*`
or `/api/board`. Hit both code paths after a UI change.

## Symptom: parallel API calls return contradictory counts for the same project

**Cause.** v1 wraps each registered project in its own lazy `IssueStore`
against its own SQLite file. The factory memoizes contexts per process,
so within one process the views are consistent — but two different
`Forge.Core` processes pointed at the same `workspace.root` will not see
each other's writes unless they share a DB file. Don't parallelize
orchestrator processes against one DB.

## Symptom: bash shell tool returns "exceeded timeout" while a Forge.Core process is alive

**Cause.** A foreground call to Forge.Core.exe (e.g. `--check`,
`--dashboard-only`) blocks the bash tool until the process exits. The
default 30 s timeout fires long before the orchestrator's normal idle
state, and the tool's kill-on-timeout behaviour can look like the model
was terminated.

**Fix.**
- Run long-lived processes via the ackground_process start action,
  not the bash tool. Track the bgp id and use Stop-Process -Id <pid>
  when done.
- When you do need foreground output, wrap your Start-Process in a
  $proc.WaitForExit(seconds) and Stop-Process -Force on the inner
  PID. The bash tool then sees a non-zero exit fast.
- Don't blanket-kill powershell / dotnet / 
ode /
  chrome / msbuild from inside the bash tool — they may be the very
  shells the orchestration client uses. If something is locked, identify
  it with Get-Process -Name "name" -ErrorAction SilentlyContinue | Select Id,ProcessName,StartTime and kill only the match.

## Symptom: a developer-facing pre-flight check returns 1 because "Workspace.Root is required"

**Cause.** You're running the pre-flight path against an older binary
(or have stale config still in ppsettings.json with an explicit
workspace.root). v1.1 dropped the Root-must-be-set check; an older
build pre-dating this commit will still fail with that message.

**Fix.**
- Pull the latest code or upgrade past commit c1XXXXX (this v1.1
  cutover). The forge will then auto-scaffold under the Forgesystem
  data root.
- Or set an explicit workspace.root pointing at an existing checkout.
