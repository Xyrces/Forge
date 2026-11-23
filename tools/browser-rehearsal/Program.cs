using Forge.Dashboard;

// Offline presentation rehearsal only: no stores, schedulers, provider clients or credentials.
var builder = WebApplication.CreateBuilder(args);
var uiAddress = new Uri(Environment.GetEnvironmentVariable("FORGE_REHEARSAL_UI_URL") ?? "http://127.0.0.1:5123");
var fixtureAddress = new Uri(Environment.GetEnvironmentVariable("FORGE_REHEARSAL_API_URL") ?? "http://127.0.0.1:5124");
if (!uiAddress.IsLoopback || !fixtureAddress.IsLoopback)
    throw new InvalidOperationException("Browser rehearsals must use loopback addresses.");
builder.WebHost.UseUrls(uiAddress.ToString());
builder.Services.AddForgeUI(builder.Environment, uiAddress, fixtureAddress);
var app = builder.Build();
app.UseAntiforgery();
app.MapForgeUI();
await app.RunAsync();
