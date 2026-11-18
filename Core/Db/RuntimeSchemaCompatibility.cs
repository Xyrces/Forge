namespace Forge.Core.Db;

/// <summary>Read compatibility only. Never use this allowance to select an initializer target.</summary>
public static class RuntimeSchemaCompatibility
{
    // Strict target: the maintenance bridge's older-version allowance ends here.
    public static bool CanRead(int version) => version == 43;
}
