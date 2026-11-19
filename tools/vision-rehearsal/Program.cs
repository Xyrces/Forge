using System.Text.Json;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Projects;
using Microsoft.Extensions.AI;

// Isolated browser qualification. Only the model provider and unrelated shell
// status responses are fixtures; UI, conversation, map, endpoints and disk writes
// execute production code. Never point --state at an existing directory.
var builder = WebApplication.CreateBuilder(args);
var state = Path.GetFullPath(builder.Configuration["state"] ?? throw new ArgumentException("--state required"));
var source = File.ReadAllText(builder.Configuration["source"] ?? throw new ArgumentException("--source required"));
if (Directory.Exists(state)) throw new InvalidOperationException("Use a new disposable state directory.");
Directory.CreateDirectory(state);
var projects = new[] { new ProjectOptions { Id = "forge", Name = "Forge", Root = Path.Combine(state, "forge") },
    new ProjectOptions { Id = "other", Name = "Other project", Root = Path.Combine(state, "other") } };
foreach (var project in projects)
{
    Directory.CreateDirectory(Path.Combine(project.Root, "docs"));
    File.WriteAllText(Path.Combine(project.Root, "docs/MASTER_DESIGN.md"), project.Id == "forge" ? source : "# Other project\n\nIndependent vision.\n");
    Git(project.Root, "init", "--quiet");
    File.WriteAllText(Path.Combine(project.Root, ".gitignore"), "state.db*\n");
    Git(project.Root, "add", ".");
    Git(project.Root, "-c", "user.name=Vision Qualification", "-c", "user.email=qualification@example.invalid", "commit", "--quiet", "-m", "Fixture vision");
}
var contexts = new ProjectContextFactory(projects,
    projects.ToDictionary(p => p.Id, p => Path.Combine(p.Root, "state.db")));
var primary = contexts.Find("forge")!;
var memory = new MemoryStore(((IssueStore)primary.Issues).Db);
var vision = new VisionStore(projects[0].Root, "docs/MASTER_DESIGN.md");
vision.Reload();
var provider = new QualificationFactory();
var address = new Uri("http://127.0.0.1:" + (builder.Configuration["port"] ?? "5126"));
builder.WebHost.UseUrls(address.ToString());
builder.Services.AddForgeUI(builder.Environment, address, address);
var app = builder.Build();
app.UseAntiforgery();
app.MapForgeUI();
var conversation = new VisionConversation(provider,
    new LlmConfig(new ProviderConfig("fixture", "http://127.0.0.1", "unused", null, "fixture")),
    app.Services.GetRequiredService<ILogger<VisionConversation>>());
VisionEndpoints.MapVisionEndpoints(app, vision, app.Logger, memory, primary.Issues, contexts, conversation);
app.MapGet("/api/projects/", () => projects.Select(p => new { p.Id, p.Name }));
app.MapGet("/api/meta/buildinfo", () => new { informationalVersion = "vision-qualification", framework = ".NET 10" });
app.MapGet("/api/health/heartbeat", () => new { status = "running", at = DateTimeOffset.UtcNow });
app.MapGet("/api/sprints/active", () => Results.Text("null", "application/json"));
app.MapGet("/__qualification/evidence", async () => new
{
    calls = provider.Calls.ToArray(),
    source = await Effective("forge"),
    other = await Effective("other"),
    repoSource = File.ReadAllText(vision.AbsolutePath),
    repoOther = File.ReadAllText(Path.Combine(projects[1].Root, "docs/MASTER_DESIGN.md")),
    gitStatus = projects.Select(p => Git(p.Root, "status", "--porcelain")).ToArray(),
    memory = (await memory.GetAsync("vision/forge"))?.Body,
});
await app.RunAsync();

async Task<string> Effective(string id)
{
    using var client = new HttpClient();
    var map = await client.GetFromJsonAsync<VisionMapDocument>(new Uri(address, $"/api/projects/{id}/vision/map"));
    return map!.Source;
}

static string Git(string root, params string[] arguments)
{
    var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = System.Diagnostics.Process.Start(start)!;
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException(error);
    return output;
}

sealed class QualificationFactory : IChatClientFactory
{
    public System.Collections.Concurrent.ConcurrentQueue<object> Calls { get; } = new();
    public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null, RoleModel? modelOverride = null)
        => new QualificationClient(this, projectId, role);
}

sealed class QualificationClient(QualificationFactory factory, string? project, AgentType role) : IChatClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var history = messages.ToArray();
        var contextText = history[1].Text;
        using var context = JsonDocument.Parse(contextText[(contextText.IndexOf('\n') + 1)..]);
        var nodes = context.RootElement.GetProperty("nodes").Deserialize<VisionMapNode[]>(Json)!;
        var pending = context.RootElement.GetProperty("pendingEdits").Deserialize<VisionMapEdit[]>(Json)!;
        var prompt = history[^1].Text;
        factory.Calls.Enqueue(new { project, role = role.ToString(), prompt,
            source = string.Concat(nodes.Select(n => n.Text)), structured = options?.ResponseFormat != null,
            tools = options?.Tools?.Count ?? 0 });
        if (prompt == "Exercise provider failure.") throw new InvalidOperationException("Qualification provider unavailable.");
        VisionMapEdit[] edits;
        if (prompt == "Keep only the sprint completion change.") edits = pending.Take(1).ToArray();
        else if (prompt == "Require UAT before sprint completion and show the blocking acceptance check.")
        {
            var sprint = nodes.First(n => n.Text.Contains("when every member task is terminal", StringComparison.Ordinal));
            var dashboard = nodes.First(n => n.Title.Contains("Dashboard", StringComparison.OrdinalIgnoreCase));
            edits = [new(sprint.Id, "replace", "when every member task is terminal", "when every member task is terminal and the sprint release passes UAT"),
                new(dashboard.Id, "append", "", "Show the acceptance check holding a sprint open.")];
        }
        else if (prompt == "Add a new root idea.") edits = [new("root", "add", "", "An independently visible new idea.", "Qualification new branch")];
        else edits = [new(nodes[0].Id, "append", "", "Qualification draft only; never publish without approval.")];
        var reply = new VisionReply("I've proposed the changes alongside the map. Nothing is saved until you apply the draft.", edits);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(reply, Json))));
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
