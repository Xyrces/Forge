using System.Data.Common;

namespace Forge.Core.Db;

public static class DbCommandExtensions
{
    /// <summary>
    /// Provider-neutral replacement for <c>Parameters.AddWithValue</c>
    /// (which only exists on provider-specific parameter collections).
    /// Null values map to DBNull.
    /// </summary>
    public static DbParameter AddParam(this DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
        return p;
    }
}
