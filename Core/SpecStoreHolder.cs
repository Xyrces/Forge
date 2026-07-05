namespace Forge.Core;

/// <summary>
/// P5 — late-binding holder for <see cref="ISpecStore"/>. The
/// orchestrator's <c>MafAgentRunner</c> is constructed before
/// the spec store (Program.cs ordering); this holder lets the
/// runner capture a forward reference via a <c>Func&lt;ISpecStore&gt;</c>
/// and resolve it on first tool build. After construction, the
/// orchestrator calls <see cref="Set"/> once when the spec
/// store is constructed. Subsequent calls to <see cref="Value"/>
/// return the same instance.
/// </summary>
public sealed class SpecStoreHolder
{
    private ISpecStore? _value;

    public ISpecStore Value => _value ?? throw new InvalidOperationException(
        "SpecStoreHolder.Value accessed before Set; Program.cs must call Set() after constructing the spec store.");

    public void Set(ISpecStore value)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }
}
