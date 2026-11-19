using Forge.Configuration;
using Forge.Core;
using Forge.Core.Db;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Forge.Dashboard;

/// <summary>Recovery-only host: never constructs normal composition, schedulers or mutating GET routes.</summary>
public static class RecoveryDashboard
{
    public static async Task RunAsync(AgentOptions options, Action? ready, CancellationToken ct)
    {
        await using var app = Build(options.Dashboard, token => ReadStateAsync(options, token));
        await app.StartAsync(ct);
        ready?.Invoke();
        await app.WaitForShutdownAsync(ct);
    }

    internal static WebApplication Build(DashboardOptions options, Func<CancellationToken, Task<object>> readState)
    {
        var builder = WebApplication.CreateSlimBuilder();
        var urls = options.Kestrel.Endpoints.Values.Select(x => x.Url).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        builder.WebHost.UseUrls(urls.Length == 0 ? [$"http://{options.Hostname}:{options.Port}"] : urls);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Method is not ("GET" or "HEAD"))
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsync("Read-only recovery: restore the environment-aware release to resume.");
                return;
            }
            await next();
        });
        app.MapGet("/api/health/heartbeat", () => Results.Ok(new { status = "healthy", recovery = true, readOnly = true, at = DateTimeOffset.UtcNow }));
        app.MapGet("/api/recovery", async (CancellationToken ct) => Results.Ok(await readState(ct)));
        app.MapGet("/", () => Results.Content(Html, "text/html"));
        app.MapFallback(() => Results.Problem("Read-only recovery: normal dashboard routes are unavailable.", statusCode: 503));
        return app;
    }

    private static async Task<object> ReadStateAsync(AgentOptions options, CancellationToken ct)
    {
        await using var registry = new IssueStore(ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.RegistrySchema, ForgeSchemaProfile.Core, initializeSchema: false));
        await using var projects = new ProjectStore(registry);
        var result = new List<object>();
        foreach (var project in await projects.ListAsync(ct))
        {
            var factory = ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.SchemaForProject(project.Id), initializeSchema: false);
            await using var connection = await factory.OpenAsync(ct);
            async Task<IReadOnlyList<object>> Rows(string table, string fields)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT TOP (20) {fields} FROM {factory.Dialect.Table(table)} ORDER BY id DESC";
                await using var reader = await command.ExecuteReaderAsync(ct);
                var rows = new List<object>();
                while (await reader.ReadAsync(ct))
                {
                    var row = new Dictionary<string, object?>();
                    for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }
                return rows;
            }
            result.Add(new { project.Id, project.Name,
                sprints = await Rows("sprint", "id,name,status"),
                deployments = await Rows("deployment", "id,commit_sha,status") });
        }
        return new { mode = "read-only-recovery", projects = result };
    }

    private const string Html = """
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Forge recovery</title>
<style>body{font:16px system-ui;background:#111827;color:#f3f4f6;max-width:1100px;margin:3rem auto;padding:1rem}table{border-collapse:collapse;width:100%;margin-bottom:2rem}td,th{text-align:left;padding:.6rem;border-bottom:1px solid #374151}h1{font-size:2rem}.notice{padding:1rem;background:#78350f;border-radius:.5rem}code{overflow-wrap:anywhere}</style>
<h1>Forge recovery</h1><p class="notice">Read-only recovery mode. Work is paused. Restore the environment-aware release to resume scheduling, approvals and deployments.</p><main id="state">Loading saved sprint and release state…</main>
<script>fetch('/api/recovery').then(r=>{if(!r.ok)throw Error('State unavailable');return r.json()}).then(x=>{const root=document.getElementById('state');root.replaceChildren();for(const p of x.projects){const title=document.createElement('h2');title.textContent=p.name;root.append(title);for(const [label,rows] of [['Sprints',p.sprints],['Deployments',p.deployments]]){const h=document.createElement('h3');h.textContent=label;root.append(h);const table=document.createElement('table');for(const row of rows){const tr=document.createElement('tr');for(const value of Object.values(row)){const td=document.createElement('td');td.textContent=value;tr.append(td)}table.append(tr)}root.append(table)}}}).catch(()=>document.getElementById('state').textContent='Saved state could not be loaded. Recovery remains read-only.');</script></html>
""";
}
