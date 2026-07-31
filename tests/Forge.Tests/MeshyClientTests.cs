using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Forge.Core;
using Forge.Meshy;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// MeshyClient tests. The HttpMessageHandler is stubbed so the
/// tests don't hit the real API. Covers the happy path for
/// text-to-3d, image-to-3d, and rigging, plus the poll loop
/// (PENDING -> SUCCEEDED) and the FAILED path. Verifies the
/// GLB download path and the local-file placement under
/// .portHorizon/art-output/.
/// </summary>
public class MeshyClientTests
{
    /// <summary>Map of request-key -> response. The key is "METHOD path".</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Dictionary<string, Queue<HttpResponseMessage>> Responses { get; } = new();
        public List<string> Calls { get; } = new();
        public int CallCount;
        public bool AllowAnyGlbDownload;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            var key = $"{request.Method} {request.RequestUri.AbsolutePath}";
            if (AllowAnyGlbDownload && request.RequestUri.Host == "signed.example")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("glb-bytes"))
                });
            }
            if (Responses.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                return Task.FromResult(queue.Dequeue());
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unhandled {key}"),
            });
        }
    }

    private MeshyClient NewClient(StubHandler handler, string? artRoot = null)
    {
        var options = Options.Create(new MeshyOptions
        {
            ApiKey = "test-key",
            BaseUrl = "https://api.test",
            PollIntervalSeconds = 1,
            MaxWaitSeconds = 10,
        });
        return new MeshyClient(handler, options,
            NullLogger<MeshyClient>.Instance,
            artOutputRoot: artRoot ?? TempRoot.Instance.NewDirectory("meshy"));
    }

    [Fact]
    public async Task SubmitTextTo3d_ReturnsTaskId()
    {
        var handler = new StubHandler();
        handler.Responses["POST /openapi/v2/text-to-3d"] = new Queue<HttpResponseMessage>();
        handler.Responses["POST /openapi/v2/text-to-3d"].Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { result = "task-001" })),
        });
        var client = NewClient(handler);
        var id = await client.SubmitTextTo3dAsync(new TextTo3dRequest { Prompt = "a crate" });
        Assert.Equal("task-001", id);
    }

    [Fact]
    public async Task WaitForTask_PollsUntilSucceeded()
    {
        var handler = new StubHandler();
        handler.Responses["GET /openapi/v2/text-to-3d/task-001"] = new Queue<HttpResponseMessage>();
        handler.Responses["GET /openapi/v2/text-to-3d/task-001"].Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { id = "task-001", status = "PENDING" })),
        });
        handler.Responses["GET /openapi/v2/text-to-3d/task-001"].Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { id = "task-001", status = "IN_PROGRESS" })),
        });
        handler.Responses["GET /openapi/v2/text-to-3d/task-001"].Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = "task-001", status = "SUCCEEDED",
                model_urls = new { glb = "https://signed.example/test.glb" },
            })),
        });
        var client = NewClient(handler);
        var rec = await client.WaitForTaskAsync("task-001", MeshyMode.TextTo3d);
        Assert.Equal("SUCCEEDED", rec.Status);
        Assert.Equal("https://signed.example/test.glb", rec.GlbUrl);
        Assert.Equal(3, handler.Calls.Count(c => c.StartsWith("GET /openapi/v2/text-to-3d/task-001")));
    }

    [Fact]
    public async Task WaitForTask_ThrowsOnFailed()
    {
        var handler = new StubHandler();
        handler.Responses["GET /openapi/v2/text-to-3d/task-002"] = new Queue<HttpResponseMessage>();
        handler.Responses["GET /openapi/v2/text-to-3d/task-002"].Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = "task-002", status = "FAILED", message = "bad prompt",
            })),
        });
        var client = NewClient(handler);
        var ex = await Assert.ThrowsAsync<MeshyException>(async () =>
            await client.WaitForTaskAsync("task-002", MeshyMode.TextTo3d));
        Assert.Contains("bad prompt", ex.Message);
    }

    [Fact]
    public async Task DownloadGlb_StreamsToLocalPath()
    {
        var handler = new StubHandler { AllowAnyGlbDownload = true };
        var root = TempRoot.Instance.NewDirectory("meshy-dl");
        var client = NewClient(handler, artRoot: root);
        var rel = await client.DownloadGlbAsync("https://signed.example/whatever.glb", "spec-x", "art-test-1");
        Assert.StartsWith("spec-x/", rel);
        Assert.EndsWith(".glb", rel);
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full));
        var bytes = await File.ReadAllBytesAsync(full);
        Assert.Equal("glb-bytes", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task SubmitImageTo3d_ReturnsTaskId()
    {
        var handler = new StubHandler();
        handler.Responses["POST /openapi/v2/image-to-3d"] = new Queue<HttpResponseMessage>();
        handler.Responses["POST /openapi/v2/image-to-3d"].Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { result = "img-task-001" })),
        });
        var client = NewClient(handler);
        var id = await client.SubmitImageTo3dAsync(new ImageTo3dRequest
        {
            ImageUrl = "data:image/png;base64,AAA",
        });
        Assert.Equal("img-task-001", id);
    }

    [Fact]
    public async Task SubmitRigging_ReturnsTaskId()
    {
        var handler = new StubHandler();
        handler.Responses["POST /openapi/v2/rigging"] = new Queue<HttpResponseMessage>();
        handler.Responses["POST /openapi/v2/rigging"].Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { result = "rig-task-001" })),
        });
        var client = NewClient(handler);
        var id = await client.SubmitRiggingAsync(new RiggingRequest
        {
            ModelUrl = "https://signed.example/prev.glb",
        });
        Assert.Equal("rig-task-001", id);
    }

    [Fact]
    public async Task SubmitFails_ThrowsMeshyException()
    {
        var handler = new StubHandler();
        // no responses queued -> 404
        var client = NewClient(handler);
        var ex = await Assert.ThrowsAsync<MeshyException>(async () =>
            await client.SubmitTextTo3dAsync(new TextTo3dRequest { Prompt = "x" }));
        Assert.Contains("POST /openapi/v2/text-to-3d failed", ex.Message);
    }
}

