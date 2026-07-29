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
        var seeded = await SkillSeeder.SeedAsync(_skills, [],
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

        // Behavior skills are Forge-owned and global (no project).
        Assert.All(all, s => { Assert.Equal(SkillSources.Forge, s.Source); Assert.Null(s.ProjectId); });
    }

    [Fact]
    public async Task Seed_IsAbsentOnly_OperatorEditsSurvive()
    {
        await SkillSeeder.SeedAsync(_skills, [], NullLogger.Instance, CancellationToken.None);

        // Operator edits the contract skill.
        var row = (await _skills.ListByRoleAsync("coredev", false, CancellationToken.None))
            .Single(s => s.Name == "forge-completion-contract");
        await _skills.UpdateAsync(row.Id, new Dictionary<string, object?> { ["body"] = "OPERATOR EDIT" }, CancellationToken.None);

        // Second seed run: must NOT overwrite the edit, must not duplicate.
        var again = await SkillSeeder.SeedAsync(_skills, [], NullLogger.Instance, CancellationToken.None);
        Assert.Equal(0, again);
        var after = (await _skills.ListByRoleAsync("coredev", false, CancellationToken.None))
            .Single(s => s.Name == "forge-completion-contract");
        Assert.Equal("OPERATOR EDIT", after.Body);
    }

    [Fact]
    public async Task Seed_KiloSkillsDir_ImportsSkillMdAsRepoOwnedProjectScoped()
    {
        var dir = Path.Combine(_workDir, "skills", "my-tool");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"),
            "---\nname: my-tool\ndescription: How to use my tool\n---\n\n# my-tool\n\nDo the thing.\n");

        await SkillSeeder.SeedAsync(_skills,
            [new SkillSeeder.ProjectSkillSource("proj1", Path.Combine(_workDir, "skills"))],
            NullLogger.Instance, CancellationToken.None);

        var all = await _skills.ListByRoleAsync(null, globalOnly: false, CancellationToken.None);
        var imported = Assert.Single(all, s => s.Name == "my-tool");
        Assert.Equal("How to use my tool", imported.Description);
        Assert.Contains("Do the thing.", imported.Body);
        Assert.True(imported.IsGlobal); // empty ROLE set = every role in the project
        Assert.Equal("proj1", imported.ProjectId);
        Assert.Equal(SkillSources.Repo, imported.Source);
    }

    [Fact]
    public async Task Seed_RepoSkills_UpsertOnChange_And_DeleteWhenRemoved()
    {
        var skillsDir = Path.Combine(_workDir, "skills");
        var dirA = Path.Combine(skillsDir, "tool-a");
        var dirB = Path.Combine(skillsDir, "tool-b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var fileA = Path.Combine(dirA, "SKILL.md");
        await File.WriteAllTextAsync(fileA, "---\nname: tool-a\n---\nv1 body\n");
        await File.WriteAllTextAsync(Path.Combine(dirB, "SKILL.md"), "---\nname: tool-b\n---\nb body\n");
        var projects = new[] { new SkillSeeder.ProjectSkillSource("proj1", skillsDir) };

        await SkillSeeder.SeedAsync(_skills, projects, NullLogger.Instance, CancellationToken.None);

        // SKILL.md edit propagates on the next seed (repo = source of truth).
        await File.WriteAllTextAsync(fileA, "---\nname: tool-a\n---\nv2 body\n");
        // SKILL.md removed from the repo removes the row.
        Directory.Delete(dirB, recursive: true);
        await SkillSeeder.SeedAsync(_skills, projects, NullLogger.Instance, CancellationToken.None);

        var all = await _skills.ListByRoleAsync(null, globalOnly: false, CancellationToken.None);
        var a = Assert.Single(all, s => s.Name == "tool-a");
        Assert.Equal("v2 body", a.Body.Trim());
        Assert.DoesNotContain(all, s => s.Name == "tool-b");
    }

    [Fact]
    public async Task Seed_RepoImport_NeverTouchesUiOwnedRows()
    {
        // A UI-owned (forge) skill with the same name as a repo file is
        // a separate row (global scope vs project scope) and survives
        // reconciliation.
        await _skills.CreateAsync(new NewSkill("tool-a", "ui body"), CancellationToken.None);
        var dirA = Path.Combine(_workDir, "skills", "tool-a");
        Directory.CreateDirectory(dirA);
        await File.WriteAllTextAsync(Path.Combine(dirA, "SKILL.md"), "---\nname: tool-a\n---\nrepo body\n");

        await SkillSeeder.SeedAsync(_skills,
            [new SkillSeeder.ProjectSkillSource("proj1", Path.Combine(_workDir, "skills"))],
            NullLogger.Instance, CancellationToken.None);
        // Repo file removed — only the repo row may go.
        Directory.Delete(dirA, recursive: true);
        await SkillSeeder.SeedAsync(_skills,
            [new SkillSeeder.ProjectSkillSource("proj1", Path.Combine(_workDir, "skills"))],
            NullLogger.Instance, CancellationToken.None);

        var all = await _skills.ListByRoleAsync(null, globalOnly: false, CancellationToken.None);
        var ui = Assert.Single(all, s => s.Name == "tool-a");
        Assert.Equal(SkillSources.Forge, ui.Source);
        Assert.Null(ui.ProjectId);
        Assert.Equal("ui body", ui.Body);
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
