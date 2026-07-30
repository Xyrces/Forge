using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Forge.Meshy;
using Xunit;
using Xunit.Abstractions;

namespace Forge.Tests.Integration;

/// <summary>
/// Live smoke test for the Meshy client. Hits the real API.
/// Skipped when no MESHY_API_KEY is set in the environment.
/// </summary>
public class MeshyLiveSmokeTests
{
    private readonly ITestOutputHelper _out;

    public MeshyLiveSmokeTests(ITestOutputHelper output) { _out = output; }

    [Fact(Timeout = 480_000)]  // up to 8 min
    public async Task TextTo3d_HappyPath_ReturnsGlbUrl()
    {
        var apiKey = Environment.GetEnvironmentVariable("MESHY_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _out.WriteLine("MESHY_API_KEY not set; skipping live smoke test.");
            return;
        }
var options = Options.Create(new MeshyOptions
        {
            ApiKey = apiKey!,
            BaseUrl = "https://api.meshy.ai",
            PollIntervalSeconds = 10,
            MaxWaitSeconds = 420,
        });
        var tmpRoot = TempRoot.Instance.NewDirectory("meshy-live");
        var client = new MeshyClient(
            new HttpClientHandler(),
            options,
            NullLogger<MeshyClient>.Instance,
            artOutputRoot: tmpRoot);

        var taskId = await client.SubmitTextTo3dAsync(new TextTo3dRequest
        {
            Prompt = "a small wooden crate, low-poly",
            ArtStyle = "realistic",
        });
        _out.WriteLine($"Submitted task: {taskId}");

        var rec = await client.WaitForTaskAsync(taskId, MeshyMode.TextTo3d);
        _out.WriteLine($"Final status: {rec.Status}");
        Assert.Equal("SUCCEEDED", rec.Status);
        Assert.False(string.IsNullOrWhiteSpace(rec.GlbUrl), "Meshy should return a glb_url on success");

        // Download the .glb to verify the download path.
        var rel = await client.DownloadGlbAsync(rec.GlbUrl!, "smoke-spec", "smoke-art");
        _out.WriteLine($"Downloaded to: {rel}");
        var full = Path.Combine(tmpRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Expected .glb at {full}");
        var bytes = await File.ReadAllBytesAsync(full);
        Assert.True(bytes.Length > 0, "Downloaded .glb is empty");
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(bytes, 0));  // "glTF" magic
    }
}

