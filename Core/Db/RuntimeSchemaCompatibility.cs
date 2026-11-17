namespace Forge.Core.Db;

/// <summary>Read compatibility only. Never use this allowance to select an initializer target.</summary>
public static class RuntimeSchemaCompatibility
{
    // Maintenance bridge: old table shape plus new persisted-format readers.
    public static bool CanRead(int version) => version is 41 or 42;
}
