using Forge.Core;

namespace Forge.Agents;

/// <summary>
/// Abstraction over the project's skill catalog. P1: backed by
/// <c>SkillStore</c> (SQLite); the runner loads matching skills and
/// appends them to the MAF agent's <c>instructions:</c> parameter.
///
/// <para>
/// <see cref="LoadForRoleAsync"/> returns the union of:
/// <list type="bullet">
///   <item>Global skills (agent_id IS NULL): visible to every role.</item>
///   <item>Role-scoped skills (agent_id = AgentRecord.Id for the role):
///   only visible to that role.</item>
/// </list>
/// </para>
///
/// <para>
/// Disabled skills (<c>enabled=0</c>) are always excluded.
/// </para>
/// </summary>
public interface ISkillSource
{
    Task<IReadOnlyList<SkillContent>> LoadForRoleAsync(
        AgentType role, CancellationToken ct = default);
}

/// <summary>
/// A single skill ready to be appended to the agent's instructions.
/// <see cref="Name"/> and <see cref="Description"/> are surfaced so the
/// LLM knows what the skill is for; <see cref="Body"/> is the actual
/// reference content (the LLM applies it, doesn't quote it).
/// </summary>
public sealed record SkillContent(
    string Name,
    string? Description,
    string Body);
