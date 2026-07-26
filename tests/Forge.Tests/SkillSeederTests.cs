using Forge.Agents;
using Forge.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// SkillSeeder: startup catalog seeding with seed-if-absent
/// semantics (operator edits are never overwritten), behavior-skill
/// role assignment, and .kilo SKILL.md import.
/// </summary>
public class SkillSeederTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly SkillStore _skills;

    public SkillSeederTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _issues = new IssueStore(Path.Combine(_workDir, "issues.db"));
        _skills = new SkillStore(_issues);
    }

    public void Dispose()
    {
        _issues.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Seed_BehaviorSkills_LandPerRole()
    {
        var seeded = await SkillSeeder.SeedAsync(_skills, kiloSkillsDir: null,
            NullLogger.Instance, CancellationToken.None);
        Assert.Equal(3, seeded); // one row per skill — roles live ON the row (many-to-many)

        var coredev = await _skills.ListByRoleAsync("coredev", globalOnly: false, CancellationToken.None);
        Assert.Contains(coredev, s => s.Name == "forge-completion-contract");
        Assert.Contains(coredev, s => s.Name == "forge-rework-protocol");
        Assert.DoesNotContain(coredev, s => s.Name == "forge-review-standards");

        var clientdev = await _skills.ListByRoleAsync("clientdev", globalOnly: false, CancellationToken.None);
        Assert.Contains(clientdev, s => s.Name == "forge-completion-contract");

        // Shared skill = ONE row visible to both roles, not duplicates.
        var all = await _skills.ListByRoleAsync(null, globalOnly: false, CancellationToken.None);
        Assert.Equal(1, all.Count(s => s.Name == "forge-completion-contract"));

        var reviewer = await _skills.ListByRoleAsync("reviewer", globalOnly: false, CancellationToken.None);
        Assert.Contains(reviewer, s => s.Name == "forge-review-standards");
    }

    [Fact]
    public async Task Seed_IsAbsentOnly_OperatorEditsSurvive()
    {
        await SkillSeeder.SeedAsync(_skills, null, NullLogger.Instance, CancellationToken.None);

        // Operator edits the contract skill.
        var row = (await _skills.ListByRoleAsync("coredev", false, CancellationToken.None))
            .Single(s => s.Name == "forge-completion-contract");
        await _skills.UpdateAsync(row.Id, new Dictionary<string, object?> { ["body"] = "OPERATOR EDIT" }, CancellationToken.None);

        // Second seed run: must NOT overwrite the edit, must not duplicate.
        var again = await SkillSeeder.SeedAsync(_skills, null, NullLogger.Instance, CancellationToken.None);
        Assert.Equal(0, again);
        var after = (await _skills.ListByRoleAsync("coredev", false, CancellationToken.None))
            .Single(s => s.Name == "forge-completion-contract");
        Assert.Equal("OPERATOR EDIT", after.Body);
    }

    [Fact]
    public async Task Seed_KiloSkillsDir_ImportsSkillMdAsGlobal()
    {
        var dir = Path.Combine(_workDir, "skills", "my-tool");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"),
            "---\nname: my-tool\ndescription: How to use my tool\n---\n\n# my-tool\n\nDo the thing.\n");

        await SkillSeeder.SeedAsync(_skills, Path.Combine(_workDir, "skills"),
            NullLogger.Instance, CancellationToken.None);

        var globals = await _skills.ListByRoleAsync(null, globalOnly: true, CancellationToken.None);
        var imported = Assert.Single(globals, s => s.Name == "my-tool");
        Assert.Equal("How to use my tool", imported.Description);
        Assert.Contains("Do the thing.", imported.Body);
        Assert.True(imported.IsGlobal);
    }

    [Fact]
    public void ParseSkillMd_HandlesFrontmatterAndPlainText()
    {
        var (name, desc, body) = SkillSeeder.ParseSkillMd("---\nname: a\ndescription: b\n---\nbody text\n");
        Assert.Equal("a", name);
        Assert.Equal("b", desc);
        Assert.Equal("body text", body.Trim());

        var (n2, d2, b2) = SkillSeeder.ParseSkillMd("no frontmatter here");
        Assert.Null(n2);
        Assert.Equal("no frontmatter here", b2);
    }
}
