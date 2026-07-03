using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortHorizon.Agents.Core;

namespace PortHorizon.Agents.Meshy;

/// <summary>
/// Thin async wrapper over the Meshy REST API. Supports
/// text-to-3d, image-to-3d, multi-image-to-3d, and rigging.
/// Long-polls the task endpoint until the task is in a terminal
/// state, then downloads the resulting <c>.glb</c> to a local
/// path under <c>.portHorizon/art-output/</c>.
///
/// <para>
/// Auth: <c>Authorization: Bearer &lt;apiKey&gt;</c>. All public
/// methods are safe to call concurrently up to
/// <see cref="MeshyOptions.MaxConcurrentJobs"/>.
/// </para>
///
/// <para>
/// Test seam: <see cref="HttpMessageHandler"/> is injectable so
/// the integration tests can stub the upstream API without
/// touching the network. In production it's wired in
/// <c>Program.cs</c> as a plain <c>SocketsHttpHandler</c>.
/// </para>
/// </summary>
public sealed class MeshyClient
{
    private readonly HttpClient _http;
    private readonly HttpMessageHandler _httpMessageHandler;
    private readonly MeshyOptions _options;
    private readonly ILogger<MeshyClient> _logger;
    private readonly string _artOutputRoot;

    public MeshyClient(
        HttpMessageHandler handler,
        IOptions<MeshyOptions> options,
        ILogger<MeshyClient> logger,
        string? artOutputRoot = null)
    {
        _options = options.Value;
        _logger = logger;
        _artOutputRoot = artOutputRoot ?? Path.Combine(".portHorizon", "art-output");
        Directory.CreateDirectory(_artOutputRoot);
        // The same handler is shared between the authenticated
        // API client and the plain .glb download client. In
        // production this is a SocketsHttpHandler (which routes
        // every request through the same underlying transport);
        // in tests this is a stub that records calls per URL.
        // Sharing the handler means the test seam can intercept
        // both the bearer-authenticated API calls AND the
        // signed-URL .glb download.
        _http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        _httpMessageHandler = handler;
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public string ArtOutputRoot => _artOutputRoot;

    /// <summary>Submit a text-to-3d job. Returns the Meshy task id.</summary>
    public Task<string> SubmitTextTo3dAsync(TextTo3dRequest req, CancellationToken ct = default)
        => SubmitAsync("/openapi/v2/text-to-3d", req, ct);

    /// <summary>Submit an image-to-3d job. <paramref name="imageUrl"/>
    /// is either a public URL or a <c>data:image/...;base64,...</c>
    /// URI (we forward as-is).</summary>
    public Task<string> SubmitImageTo3dAsync(ImageTo3dRequest req, CancellationToken ct = default)
        => SubmitAsync("/openapi/v2/image-to-3d", req, ct);

    /// <summary>Submit a multi-image-to-3d job.</summary>
    public Task<string> SubmitMultiImageTo3dAsync(MultiImageTo3dRequest req, CancellationToken ct = default)
        => SubmitAsync("/openapi/v2/multi-image-to-3d", req, ct);

    /// <summary>Submit a rigging job. <paramref name="modelUrl"/>
    /// is the input GLB (typically the output of a previous
    /// text-to-3d or image-to-3d run).</summary>
    public Task<string> SubmitRiggingAsync(RiggingRequest req, CancellationToken ct = default)
        => SubmitAsync("/openapi/v2/rigging", req, ct);

    private async Task<string> SubmitAsync<T>(string path, T req, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(path, req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new MeshyException(
                $"Meshy POST {path} failed: {(int)resp.StatusCode} {resp.StatusCode} body={body}");
        }
        var parsed = JsonSerializer.Deserialize<MeshySubmitResponse>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new MeshyException("Meshy returned an empty submit body");
        if (string.IsNullOrWhiteSpace(parsed.Result))
            throw new MeshyException($"Meshy returned no task id: {body}");
        return parsed.Result;
    }

    /// <summary>Poll a task until terminal. Returns the task record
    /// with the final status + the signed GLB URL.</summary>
    public async Task<MeshyTaskRecord> WaitForTaskAsync(
        string taskId, MeshyMode mode, CancellationToken ct = default)
    {
        var path = mode switch
        {
            MeshyMode.TextTo3d => $"/openapi/v2/text-to-3d/{taskId}",
            MeshyMode.ImageTo3d => $"/openapi/v2/image-to-3d/{taskId}",
            MeshyMode.MultiImageTo3d => $"/openapi/v2/multi-image-to-3d/{taskId}",
            MeshyMode.Rigging => $"/openapi/v2/rigging/{taskId}",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(_options.MaxWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var resp = await _http.GetAsync(path, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                throw new MeshyException(
                    $"Meshy GET {path} failed: {(int)resp.StatusCode} {resp.StatusCode} body={body}");
            }
            var task = JsonSerializer.Deserialize<MeshyTaskResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new MeshyException("Meshy returned an empty task body");
            switch (task.Status)
            {
                case "SUCCEEDED":
                    return new MeshyTaskRecord(
                        Id: taskId,
                        Mode: mode.ToString(),
                        Status: task.Status,
                        ArtOutputId: null,
                        GlbUrl: task.ModelUrls?.Glb);
                case "FAILED":
                case "CANCELED":
                    throw new MeshyException(
                        $"Meshy task {taskId} ended in {task.Status}: {task.Message ?? "(no message)"}");
                default: // PENDING | IN_PROGRESS
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct);
                    break;
            }
        }
        throw new MeshyException($"Meshy task {taskId} timed out after {_options.MaxWaitSeconds}s");
    }

    /// <summary>Download a signed GLB URL to a stable local path.
    /// Returns the relative path under
    /// <see cref="ArtOutputRoot"/>, suitable for the
    /// <c>art_output.body</c> column.</summary>
    public async Task<string> DownloadGlbAsync(
        string glbUrl, string specId, string artId, CancellationToken ct = default)
    {
        var specDir = Path.Combine(_artOutputRoot, specId);
        Directory.CreateDirectory(specDir);
        var fileName = $"{artId}.glb";
        var fullPath = Path.Combine(specDir, fileName);
        // The signed URL is on a different host than the API; the
        // shared HttpMessageHandler can still route it through the
        // same transport. We use a plain HttpClient (no auth
        // header) for the download so we don't leak the bearer
        // token to a third-party signed-URL host.
        using var plain = new HttpClient(_httpMessageHandler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(120),
        };
        using var resp = await plain.GetAsync(glbUrl, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new MeshyException(
                $"Meshy GLB download failed: {(int)resp.StatusCode} {resp.StatusCode}");
        }
        await using var fs = File.Create(fullPath);
        await resp.Content.CopyToAsync(fs, ct);
        // Store the relative path (the dashboard's Art tab is served
        // from .portHorizon/art-output/; an absolute path would
        // embed C:\Users\... in the DB).
        return Path.Combine(specId, fileName).Replace('\\', '/');
    }
}

public enum MeshyMode { TextTo3d, ImageTo3d, MultiImageTo3d, Rigging }

public sealed class MeshyOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.meshy.ai";
    public int PollIntervalSeconds { get; set; } = 5;
    public int MaxWaitSeconds { get; set; } = 600;
    public int MaxConcurrentJobs { get; set; } = 4;
}

public sealed class TextTo3dRequest
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "preview";
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = string.Empty;
    [JsonPropertyName("ai_model")] public string AiModel { get; set; } = "meshy-6";
    [JsonPropertyName("art_style")] public string? ArtStyle { get; set; } = "realistic";
    [JsonPropertyName("negative_prompt")] public string? NegativePrompt { get; set; }
    [JsonPropertyName("remesh")] public bool? Remesh { get; set; }
    [JsonPropertyName("should_remesh")] public bool? ShouldRemesh { get; set; }
    [JsonPropertyName("should_texture")] public bool? ShouldTexture { get; set; }
}

public sealed class ImageTo3dRequest
{
    [JsonPropertyName("image_url")] public string ImageUrl { get; set; } = string.Empty;
    [JsonPropertyName("ai_model")] public string AiModel { get; set; } = "meshy-6";
    [JsonPropertyName("remesh")] public bool? Remesh { get; set; }
    [JsonPropertyName("should_remesh")] public bool? ShouldRemesh { get; set; }
    [JsonPropertyName("should_texture")] public bool? ShouldTexture { get; set; }
}

public sealed class MultiImageTo3dRequest
{
    [JsonPropertyName("image_urls")] public string[] ImageUrls { get; set; } = Array.Empty<string>();
    [JsonPropertyName("ai_model")] public string AiModel { get; set; } = "meshy-6";
}

public sealed class RiggingRequest
{
    [JsonPropertyName("model_url")] public string ModelUrl { get; set; } = string.Empty;
    [JsonPropertyName("height_m")] public double? HeightMeters { get; set; }
}

public sealed class MeshySubmitResponse
{
    [JsonPropertyName("result")] public string? Result { get; set; }
}

public sealed class MeshyTaskResponse
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("model_urls")] public MeshyModelUrls? ModelUrls { get; set; }
    [JsonPropertyName("thumbnail_url")] public string? ThumbnailUrl { get; set; }
    [JsonPropertyName("progress")] public int? Progress { get; set; }
}

public sealed class MeshyModelUrls
{
    [JsonPropertyName("glb")] public string? Glb { get; set; }
    [JsonPropertyName("fbx")] public string? Fbx { get; set; }
    [JsonPropertyName("obj")] public string? Obj { get; set; }
    [JsonPropertyName("usdz")] public string? Usdz { get; set; }
}

public sealed class MeshyException : Exception
{
    public MeshyException(string message) : base(message) { }
    public MeshyException(string message, Exception inner) : base(message, inner) { }
}
