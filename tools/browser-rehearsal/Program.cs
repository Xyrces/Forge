using Forge.Dashboard;

// Offline presentation rehearsal only: no stores, schedulers, provider clients or credentials.
var builder = WebApplication.CreateBuilder(args);
var uiAddress = new Uri("http://127.0.0.1:5123");
var fixtureAddress = new Uri("http://127.0.0.1:5124");
builder.WebHost.UseUrls(uiAddress.ToString());
builder.Services.AddForgeUI(builder.Environment, uiAddress, fixtureAddress);
var app = builder.Build();
app.UseAntiforgery();
app.MapForgeUI();
await app.RunAsync();
