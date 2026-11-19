using System.Data.Common;
using System.Text.Json;
using Forge.Core.Db;

namespace Forge.Core;

/// <summary>Write barrier independent of the compatibility bridge's initializer version.</summary>
public static class ImplementationEvidenceFormat
{
    public const int MinimumVersion = 42;

    public static void RejectReservedProofKeys(IEnumerable<string>? keys)
    {
        if (keys?.Any(key => (key.Equals("trustedExistingImplementation", StringComparison.OrdinalIgnoreCase) || key.Equals("trustedIntegrationVerification", StringComparison.OrdinalIgnoreCase))) == true)
            throw new InvalidOperationException("Existing implementation proof can only be written by canonical independent evidence admission.");
    }

    public static async Task ValidateJsonAsync(DbConnection connection, DbTransaction? transaction,
        ISqlDialect dialect, string json, CancellationToken ct = default)
    {
        using var document = JsonDocument.Parse(json);
        if (ContainsNewFormat(document.RootElement, 0))
            await RequireAsync(connection, transaction, dialect, ct);
    }

    public static async Task RequireAsync(DbConnection connection, DbTransaction? transaction,
        ISqlDialect dialect, CancellationToken ct = default)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COALESCE(MAX(version),0) FROM {dialect.Table("schema_version")}";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) < MinimumVersion)
            throw new InvalidOperationException("ImplementationVerified requires the explicit schema-42 format upgrade before persistence.");
    }

    private static bool ContainsNewFormat(JsonElement value, int depth)
    {
        if (depth > 32) throw new InvalidOperationException("Evidence metadata nesting exceeds the supported format.");
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Any(item => ContainsNewFormat(item, depth + 1));
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name.Equals("trustedExistingImplementation", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind is not JsonValueKind.Null) return true;
            if (property.Name.Equals("evidenceKind", StringComparison.OrdinalIgnoreCase)
                && (property.Value.ValueKind == JsonValueKind.String
                    && (property.Value.GetString()?.Trim().Equals(nameof(SprintEvidenceKind.ImplementationVerified), StringComparison.OrdinalIgnoreCase) == true
                        || int.TryParse(property.Value.GetString(), out var numericText) && numericText == (int)SprintEvidenceKind.ImplementationVerified)
                    || property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out var number) && number == (int)SprintEvidenceKind.ImplementationVerified)) return true;
            // Historical metadata stores the binding either as an object or as encoded JSON.
            if (property.Name.Equals(SprintPlanJson.MetadataKey, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                if (ContainsEncodedBinding(property.Value.GetString()!, depth + 1)) return true;
            }
            if (ContainsNewFormat(property.Value, depth + 1)) return true;
        }
        return false;
    }

    private static bool ContainsEncodedBinding(string json, int depth)
    {
        try
        {
            using var binding = JsonDocument.Parse(json);
            return ContainsNewFormat(binding.RootElement, depth);
        }
        catch (JsonException)
        {
            // An unreadable historical binding is not new-format evidence. Keep
            // unrelated park/close operations available; typed binding admission
            // and plan validation still reject it as an acceptance mapping.
            return false;
        }
    }
}
