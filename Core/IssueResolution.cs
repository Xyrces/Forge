using Forge.Core.Db;

namespace Forge.Core;

public enum CloseDispositionKind { Unknown, Cancelled, DeliveredElsewhere, DuplicateDelivered }
public sealed record CloseDisposition(CloseDispositionKind Kind, string Reason, string? Evidence = null, string? ReferenceIssueId = null);

/// <summary>Terminal is not synonymous with delivered. Legacy Closed rows have unknown disposition.</summary>
public static class IssueResolution
{
    public static bool IsSatisfied(IssueRecord issue, IReadOnlyDictionary<string, IssueRecord>? context = null)
    {
        if (issue.Status == IssueStatus.Completed) return true;
        if (issue.Status != IssueStatus.Closed || string.IsNullOrWhiteSpace(issue.GetMetadata("closeEvidence"))) return false;
        var kind = issue.GetMetadata("closeDisposition");
        if (kind == nameof(CloseDispositionKind.DeliveredElsewhere)) return true;
        return kind == nameof(CloseDispositionKind.DuplicateDelivered)
            && context is not null && issue.GetMetadata("closeReferenceIssueId") is { } id
            && context.TryGetValue(id, out var referenced) && referenced.Status == IssueStatus.Completed;
    }

    internal static string SatisfiedSql(string alias, ISqlDialect dialect)
    {
        string Value(string key) => dialect.Provider == ForgeDbProvider.SqlServer
            ? $"JSON_VALUE({alias}.metadata_json,'$.{key}')" : $"json_extract({alias}.metadata_json,'$.{key}')";
        return $"({alias}.status='Completed' OR ({alias}.status='Closed' AND COALESCE({Value("closeEvidence")},'')<>'' AND "
            + $"({Value("closeDisposition")}='DeliveredElsewhere' OR ({Value("closeDisposition")}='DuplicateDelivered' AND EXISTS(SELECT 1 FROM {dialect.Table("issue")} resolved WHERE resolved.id={Value("closeReferenceIssueId")} AND resolved.status='Completed')))))";
    }
}
