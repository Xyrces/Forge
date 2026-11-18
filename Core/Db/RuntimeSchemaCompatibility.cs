namespace Forge.Core.Db;

/// <summary>Read compatibility only. Never use this allowance to select an initializer target.</summary>
public static class RuntimeSchemaCompatibility
{
    // The additive owner-history table does not change format42 records.
    // Keep initialization and routine releases pinned to42 while rollback can read43.
    public static bool CanRead(int version) => version is 42 or 43;
}
