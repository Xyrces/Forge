using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Initiatives;
using Forge.Dashboard;
using Forge.Projects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

// Production UI, endpoints, intake agent and approval service over disposable stores.
// No production config, remote providers, scheduler, credentials or deployment code.
var builder = WebApplication.CreateBuilder(args);
var state = Path.GetFullPath(builder.Configuration["state"] ?? throw new ArgumentException("--state required"));
if (Directory.Exists(state)) throw new InvalidOperationException("Use a new disposable state directory.");
Directory.CreateDirectory(state);
var projects = new[] { new ProjectOptions { Id = "forge", Name = "Forge", Root = Path.Combine(state, "forge") },
    new ProjectOptions { Id = "other", Name = "Other project", Root = Path.Combine(state, "other") } };
foreach (var p in projects) Directory.CreateDirectory(p.Root);
var contexts = new ProjectContextFactory(projects, projects.ToDictionary(p => p.Id, p => Path.Combine(p.Root, "state.db")));
var primary = (IssueStore)contexts.Find("forge")!.Issues;
var intake = new IntakeStore(primary);
var proposals = new InitiativeProposalStore(primary);
var events = new InMemoryDashboardEventBus();
InitiativeIngestionStore Ingestion(string id) => new((IssueStore)(contexts.Find(id)?.Issues ?? throw new InvalidOperationException("Unknown project")));
var approvals = new InitiativeApprovalService(proposals, Ingestion, events);
var provider = new FixtureFactory();
var registry = new IntakeAgentRegistry(id => new IntakeAgent(id, intake, contexts.Find(id)!.Issues,
    contexts.Find(id)!.Sprints, provider, new LlmConfig(new ProviderConfig("fixture", "", null, null, "fixture")),
    new RoleAgentRegistry(), events, NullLogger<IntakeAgent>.Instance, initiatives: approvals));
var session = await intake.CreateAsync("forge", "Clear release evidence");
await intake.AppendMessageAsync(session.Id, new(IntakeMessageRole.User, "Make release evidence clear without starting more work."));
await intake.AppendMessageAsync(session.Id, new(IntakeMessageRole.Assistant, "The brief captures this scope. Review the changes before approving."));
var initial = await proposals.CreateAsync(new("forge", "qualification-release", "user", "Clear release evidence", "## Outcome\nExplain release evidence.\n\n## Acceptance\n- Show the deployed version.\n\n## Out of scope\nDeployment changes.", SessionId: session.Id));
var revised = await proposals.ReviseAsync("forge", initial.Id, 1, initial.Title, "## Outcome\nExplain release evidence clearly.\n\n## Acceptance\n- Show the deployed version.\n- Link the verification result.\n\n## Out of scope\nDeployment changes.", 1);
var sibling = await proposals.CreateAsync(new("forge", "qualification-sibling", "user", "Keep the evidence readable", "## Outcome\nReadable evidence.\n\n## Acceptance\n- Use plain language.", SessionId: session.Id));
var suggestion = await proposals.CreateAsync(new("forge", "qualification-suggestion", "forge", "Explain blocked releases", "## Outcome\nShow what needs attention."));
var otherSession = await intake.CreateAsync("other", "Other project conversation");
var other = await proposals.CreateAsync(new("other", "qualification-other", "user", "Other project private brief", "## Outcome\nIndependent project scope.", SessionId: otherSession.Id));
var address = new Uri("http://127.0.0.1:" + (builder.Configuration["port"] ?? "5127"));
builder.WebHost.UseUrls(address.ToString());
builder.Services.AddForgeUI(builder.Environment, address, address);
var app = builder.Build();
var approvalRequests = new System.Collections.Concurrent.ConcurrentQueue<System.Text.Json.JsonElement>();
app.Use(async (context, next) => {
    if (context.Request.Method == "POST" && context.Request.Path.Value?.EndsWith("/approve", StringComparison.Ordinal) == true)
    {
        context.Request.EnableBuffering();
        using var document = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
        approvalRequests.Enqueue(document.RootElement.Clone());
        context.Request.Body.Position = 0;
    }
    await next(context);
});
app.UseAntiforgery();
app.MapForgeUI();
IntakeEndpoints.MapIntakeEndpoints(app, registry, primary, contexts.Find("forge")!.Sprints, intake, app.Logger, approvals);
SpecEndpoints.MapSpecEndpoints(app, contexts.Find("forge")!.Specs, new NullSpecExtractionReader(), app.Logger, intake,
    projectContexts: contexts, issues: primary, initiativeApprovals: approvals);
app.MapGet("/api/projects/", () => projects.Select(p => new { p.Id, p.Name }));
app.MapGet("/api/meta/buildinfo", () => new { informationalVersion = "intake-qualification", framework = ".NET 10" });
app.MapGet("/api/health/heartbeat", () => new { status = "running", at = DateTimeOffset.UtcNow });
app.MapGet("/api/sprints/active", () => Results.Text("null", "application/json"));
app.MapGet("/__qualification/seed", () => new { session, initial, revised, sibling, suggestion, otherSession, other });
app.MapGet("/__qualification/evidence", async () => new {
    calls = provider.Calls, approvalRequests = approvalRequests.ToArray(),
    forgeIssues = await primary.ListAsync(new IssueFilter()),
    forgeSpecs = await contexts.Find("forge")!.Specs.ListAsync("forge", null),
    otherIssues = await contexts.Find("other")!.Issues.ListAsync(new IssueFilter()),
    otherSpecs = await contexts.Find("other")!.Specs.ListAsync("other", null),
    sprints = await contexts.Find("forge")!.Sprints.ListAsync(false),
    sessions = await intake.ListAsync(),
    receipt = await approvals.ReceiptAsync("forge", revised.Id, revised.Revision)
});
// Test-only concurrent editor; normal UI approval must reject this stale snapshot.
app.MapPost("/__qualification/revise/{id}", async (string id) => {
    var p = await proposals.GetAsync("forge", id) ?? throw new InvalidOperationException("Missing fixture");
    return await proposals.ReviseAsync("forge", id, p.Revision, p.Title, p.Body + "\n\nConcurrent editor change.", p.Priority);
});
await app.RunAsync();

sealed class FixtureFactory : IChatClientFactory
{
    public int Calls;
    public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null, RoleModel? modelOverride = null) => new FixtureClient(this);
}
sealed class FixtureClient(FixtureFactory owner) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref owner.Calls);
        var history = messages.ToArray();
        if (history.LastOrDefault()?.Role != ChatRole.Tool
            && history.LastOrDefault(m => m.Role == ChatRole.User)?.Text == "Draft the release verification brief.")
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("qualification-proposal", "create_epic", new Dictionary<string, object?>
                {
                    ["title"] = "Release verification from conversation",
                    ["description"] = "## Outcome\nKeep the operator informed.\n\n## Acceptance\n- Show the verification outcome.\n\n## Out of scope\nDo not schedule engineering during this test.",
                    ["priority"] = 2
                })])));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Let's clarify the desired outcome and what would count as success. Nothing has been approved.")));
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
