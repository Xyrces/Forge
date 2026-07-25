using Forge.Core;
using Microsoft.Extensions.Logging;

namespace Forge.Agents;

/// <summary>
/// Seeds the skill catalog on startup with seed-if-absent semantics:
/// a skill row is created ONLY when (name, role) has no row yet —
/// operator edits made via the dashboard are never overwritten.
///
/// Two sources:
/// <list type="number">
/// <item>Pipeline-behavior skills (canonical text embedded here) —
/// the orchestration's behavioral contract, assigned per role
/// (completion contract, rework protocol, review standards).</item>
/// <item>The project's <c>.kilo/skills/&lt;name&gt;/SKILL.md</c>
/// files — imported as global skills (dogfooding knowledge that
/// ships with the repo).</item>
/// </list>
/// </summary>
public static class SkillSeeder
{
    public static async Task<int> SeedAsync(
        ISkillStore skills, string? kiloSkillsDir, ILogger logger, CancellationToken ct = default)
    {
        var existing = (await skills.ListByRoleAsync(role: null, globalOnly: false, ct))
            .Select(s => (s.Name, s.Role ?? ""))
            .ToHashSet();

        var seeded = 0;
        foreach (var (name, description, body, roles) in BehaviorSkills)
        {
            foreach (var role in roles)
            {
                if (existing.Contains((name, role))) continue;
                await skills.CreateAsync(new NewSkill(
                    Name: name, Body: body, Description: description, Role: role), ct);
                seeded++;
            }
        }

        if (kiloSkillsDir is not null && Directory.Exists(kiloSkillsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(kiloSkillsDir))
            {
                var file = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(file)) continue;
                var (name, description, body) = ParseSkillMd(await File.ReadAllTextAsync(file, ct));
                if (name is null || existing.Contains((name, ""))) continue;
                await skills.CreateAsync(new NewSkill(Name: name, Body: body, Description: description), ct);
                seeded++;
            }
        }

        if (seeded > 0)
            logger.LogInformation("Skill catalog: seeded {Count} new skills", seeded);
        return seeded;
    }

    /// <summary>Parse a .kilo SKILL.md: YAML frontmatter (name,
    /// description) + markdown body.</summary>
    internal static (string? Name, string? Description, string Body) ParseSkillMd(string text)
    {
        if (!text.StartsWith("---", StringComparison.Ordinal)) return (null, null, text);
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (null, null, text);
        var front = text[3..end];
        var body = text[(end + 4)..].TrimStart('\r', '\n');
        string? name = null, description = null;
        foreach (var line in front.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (key == "name") name = value;
            else if (key == "description") description = value;
        }
        return (name, description, body);
    }

    private static readonly (string Name, string? Description, string Body, string[] Roles)[] BehaviorSkills =
    {
        ("forge-completion-contract",
         "How an engineering run must end: work delivered (edit, build, test, push branch — never open a PR) or an evidence-backed no-op.",
         """
         # Forge completion contract

         You are an engineering agent in the Forge dispatch loop. Every run must end in one of exactly two ways:

         1. **Work delivered.** Explore first, then EDIT files, then build and run the relevant tests. Commit your changes and push your branch. Do NOT open a pull request — the orchestrator opens it (a second PR for the same branch is rejected by GitHub).

         2. **Verified no-op.** If — and only if — you conclude the task genuinely requires no code changes, end your final message with the exact marker `NO_CHANGES_NEEDED`, a one-sentence justification, AND the evidence you gathered: what you checked (files read, greps run) and what you observed that proves the work already exists. A no-op claim without concrete evidence is treated as a failed attempt and re-queued; three no-progress attempts fail the task for operator review.

         Never leave the worktree dirty without committing. Never restructure unrelated code.
         """,
         new[] { "coredev", "clientdev" }),

        ("forge-rework-protocol",
         "What a rework round means and how to run one: same branch/PR, fix the findings, resolve conflicts minimally, push and stop.",
         """
         # Forge rework protocol

         A rework round means your earlier PR failed review or CI. The dispatch prompt includes the failure context.

         - Work on the SAME branch and the SAME PR. Do not restructure unrelated work; address the findings.
         - Reviewer findings: fix each REVIEWER_NOTES item, or explain in your final message why a finding is wrong.
         - Merge conflicts with main: `git fetch origin && git merge origin/main`, resolve conflicts minimally, run the full test suite, push. Keep your earlier changes intact.
         - After pushing, stop. The watch sweep re-reviews the new head and handles merge.
         """,
         new[] { "coredev", "clientdev" }),

        ("forge-review-standards",
         "Verdict rules for PR review: request-changes only for blocking findings; non-blocking observations become follow-ups.",
         """
         # Forge review standards

         You review PRs in the Forge loop. Verdict rules:

         - **REQUEST_CHANGES only for blocking findings**: correctness bugs, broken or missing tests for changed behavior, architecture-boundary violations (module rules), scope creep, dead code introduced by the diff.
         - **Non-blocking observations** (style nits, follow-up refactors, adjacent tech debt) are NOT changes: approve and file them via `file_followup` so they re-enter through grooming.
         - Always review at the current head SHA — a verdict against a stale head is discarded.
         - Output format: a concise assessment, then a REVIEWER_NOTES list (file:line per finding), then the verdict line `REVIEWER_VERDICT: APPROVE` or `REVIEWER_VERDICT: REQUEST_CHANGES`.
         """,
         new[] { "reviewer" }),
    };
}
