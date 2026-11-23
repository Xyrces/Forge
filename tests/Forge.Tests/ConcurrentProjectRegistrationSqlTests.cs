using Forge.Core;
using Forge.Core.Db;
using Xunit;

namespace Forge.Tests;

public sealed class ConcurrentProjectRegistrationSqlTests
{
    private const string ConnectionVariable = "FORGE_TEST_REGISTRATION_SQL";

    private sealed class RegistrationSqlFactAttribute : FactAttribute
    {
        public RegistrationSqlFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = "Requires a disposable SQL schema on the Linux registration test database.";
        }
    }

    [RegistrationSqlFact]
    public async Task ConcurrentFirstRegistrationsCreateOneRowAndPreserveTheExistingProject()
    {
        var schema = $"audit_registration_{Guid.NewGuid():N}";
        var db = ForgeDb.SqlServer(Environment.GetEnvironmentVariable(ConnectionVariable)!, schema,
            ForgeSchemaProfile.Core, initializeSchema: false);
        await using var connection = await db.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA [{schema}]";
        await command.ExecuteNonQueryAsync();
        try
        {
            var table = db.Dialect.Table("project");
            command.CommandText = $$"""
                CREATE TABLE {{table}} (
                    id nvarchar(128) PRIMARY KEY, name nvarchar(max) NOT NULL, repo_url nvarchar(max) NOT NULL,
                    default_branch nvarchar(128) NOT NULL, local_path nvarchar(max), created_at nvarchar(64) NOT NULL,
                    updated_at nvarchar(64) NOT NULL, last_synced_at nvarchar(64), last_sync_error nvarchar(max),
                    roles_json nvarchar(max) NOT NULL DEFAULT '{}')
                """;
            await command.ExecuteNonQueryAsync();
            await using var issues = new IssueStore(db);
            await using var projects = new ProjectStore(issues);
            var first = await projects.UpsertAsync(new("first", "First", "https://example.invalid/first"));
            var second = new NewProject("second", "Second", "https://example.invalid/second");
            var registrations = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => projects.UpsertAsync(second)));
            Assert.All(registrations, row => Assert.Equal(registrations[0].CreatedAt, row.CreatedAt));
            var rows = await projects.ListAsync();
            Assert.Equal(new[] { first.Id, second.Id }, rows.Select(row => row.Id));
            Assert.Equal(first.UpdatedAt, rows[0].UpdatedAt);
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [{schema}].[project]; DROP SCHEMA [{schema}];";
            await command.ExecuteNonQueryAsync();
        }
    }

    [RegistrationSqlFact]
    public async Task WorkerEnvironmentApprovalAndConcurrentSettingsUseAtomicRegistryWrites()
    {
        var schema = $"audit_worker_env_{Guid.NewGuid():N}";
        var db = ForgeDb.SqlServer(Environment.GetEnvironmentVariable(ConnectionVariable)!, schema,
            ForgeSchemaProfile.Core, initializeSchema: false);
        await using var connection = await db.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA [{schema}]";
        await command.ExecuteNonQueryAsync();
        try
        {
            command.CommandText = $$"""
                CREATE TABLE {{db.Dialect.Table("project")}} (
                    id nvarchar(128) PRIMARY KEY, name nvarchar(max) NOT NULL, repo_url nvarchar(max) NOT NULL,
                    default_branch nvarchar(128) NOT NULL, local_path nvarchar(max), created_at nvarchar(64) NOT NULL,
                    updated_at nvarchar(64) NOT NULL, last_synced_at nvarchar(64), last_sync_error nvarchar(max),
                    roles_json nvarchar(max) NOT NULL DEFAULT '{}')
                """;
            await command.ExecuteNonQueryAsync();
            await using var issues = new IssueStore(db);
            await using var projects = new ProjectStore(issues);
            var environments = new ProjectWorkerEnvironmentStore(projects);
            await projects.UpsertAsync(new("first", "First", "https://example.invalid/first"));
            await projects.UpsertAsync(new("second", "Second", "https://example.invalid/second"));
            var definition = (await environments.SetDefinitionAsync("second", new("0", "Dockerfile")))!;
            var approval = new QualifyWorkerEnvironment(definition.Revision, definition.DefinitionRevision,
                "registry.example.invalid/worker@sha256:" + new string('a', 64), new string('b', 40));
            await Task.WhenAll(
                environments.QualifyAsync("second", approval),
                projects.UpdateRolesAsync("second", new Dictionary<string, int> { ["coredev"] = 3 }),
                projects.UpdateVerifyCommandsAsync("second", ["make test"]),
                projects.UpdateQaAsync("second", true));
            Assert.Equal(approval.Image, await environments.ResolveImageAsync("second"));
            Assert.Null(await environments.ResolveImageAsync("first"));
            Assert.Equal(3, (await projects.GetAsync("second"))!.Roles["coredev"]);
            Assert.Equal(new[] { "make test" }, (await projects.GetAsync("second"))!.VerifyCommands);
            Assert.True((await projects.GetAsync("second"))!.QaEnabled);
            await Assert.ThrowsAsync<ProjectSettingsConflictException>(() => environments.QualifyAsync("second", approval));
            var active = (await environments.GetAsync("second"))!;
            var edited = (await environments.SetDefinitionAsync("second", new(active.Revision, "tools/Dockerfile")))!;
            Assert.Equal("pending", edited.Status);
            Assert.Equal(approval.Image, await environments.ResolveImageAsync("second"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [{schema}].[project]; DROP SCHEMA [{schema}];";
            await command.ExecuteNonQueryAsync();
        }
    }
}
