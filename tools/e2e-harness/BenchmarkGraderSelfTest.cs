using System.Diagnostics;
using System.Text.Json;

namespace Forge.Tools.E2E;

internal static class BenchmarkGraderSelfTest
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-benchmark-grader-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            foreach (var caseId in new[] { "calculator", "normalize", "invoice" })
            {
                var fixture = BenchmarkFixture.Get(caseId);
                var variants = new[]
                {
                    (Name: "starter", Source: fixture.StarterSource, ExpectedPass: false),
                    (Name: "known-bad", Source: fixture.KnownBadSolution, ExpectedPass: false),
                    (Name: "good", Source: fixture.FakeSolution, ExpectedPass: true),
                };
                foreach (var variant in variants)
                {
                    var variantRoot = Path.Combine(root, caseId, variant.Name);
                    var implementationRoot = Path.Combine(variantRoot, "implementation");
                    var graderRoot = Path.Combine(variantRoot, "trusted-grader");
                    var reportPath = Path.Combine(variantRoot, "report.json");
                    Directory.CreateDirectory(implementationRoot);
                    fixture.WriteScaffold(implementationRoot);
                    File.WriteAllText(Path.Combine(implementationRoot, fixture.ImplementationPath), variant.Source);
                    fixture.WriteTrustedGrader(graderRoot, implementationRoot, reportPath);

                    var exitCode = await RunAsync(graderRoot, variantRoot, cancellationToken);
                    var passed = exitCode == 0;
                    Console.WriteLine($"grader self-test: {caseId}/{variant.Name} exit={exitCode}");
                    var report = File.Exists(reportPath)
                        ? JsonSerializer.Deserialize(
                            await File.ReadAllTextAsync(reportPath, cancellationToken),
                            BenchmarkJsonContext.Default.GraderReport)
                        : null;
                    var behaviorIsValid = variant.Name switch
                    {
                        "starter" => !passed,
                        "known-bad" => !passed && report?.Checks.Any(static c => !c.Passed) == true,
                        "good" => passed && report?.Checks.Count > 0
                            && report.Checks.All(static c => c.Passed),
                        _ => false,
                    };
                    if (!behaviorIsValid)
                    {
                        Console.Error.WriteLine(
                            $"Grader self-test failed: {caseId}/{variant.Name} expected pass={variant.ExpectedPass}, " +
                            $"actual={passed}, report={(report is null ? "missing" : "present")}.");
                        return 1;
                    }
                }
            }
            Console.WriteLine("PASS: every trusted grader rejects its starter and known-bad implementation and accepts the reference solution.");
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* best-effort cleanup of harness-owned temporary state */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup of harness-owned temporary state */ }
        }
    }

    private static async Task<int> RunAsync(
        string graderRoot,
        string variantRoot,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = graderRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(Path.Combine(graderRoot, "Grader.csproj"));
        start.ArgumentList.Add("--nologo");
        start.ArgumentList.Add("--verbosity");
        start.ArgumentList.Add("quiet");
        start.Environment["DOTNET_CLI_HOME"] = Path.Combine(variantRoot, "dotnet-home");
        start.Environment["NUGET_PACKAGES"] = Path.Combine(variantRoot, "nuget-packages");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start grader self-test process.");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        _ = await stdout;
        _ = await stderr;
        return process.ExitCode;
    }
}
