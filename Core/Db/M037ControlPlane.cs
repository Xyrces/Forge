using System.Data.Common;
using Forge.Core.Control;

namespace Forge.Core.Db;

public sealed class M037ControlPlane : ISqlServerMigration
{
    public int Version => 37;
    public string Name => "control-plane-and-budget";
    public void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile == ForgeSchemaProfile.Core) ControlSchema.Create(conn, dialect);
    }
}
