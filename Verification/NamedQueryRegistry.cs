namespace NasFileMonitor.Verification;

public interface INamedQueryRegistry
{
    bool ContainsKey(string queryKey);
    string GetSql(string queryKey);
}

public sealed class NamedQueryRegistry : INamedQueryRegistry
{
    private static readonly IReadOnlyDictionary<string, string> Queries =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CorrespondenceStatusByFileName"] = """
                SELECT k.[value] AS FileKey, t.ProcessingStatus AS Status
                FROM OPENJSON(@FileKeysJson) k
                INNER JOIN dbo.CorrespondenceProcessingLog t WITH (NOLOCK)
                    ON t.FileName = k.[value]
                """,
            ["WireStatusByFileName"] = """
                SELECT k.[value] AS FileKey, t.StatusCode AS Status
                FROM OPENJSON(@FileKeysJson) k
                INNER JOIN dbo.WireAuditLog t WITH (NOLOCK)
                    ON t.SourceFileName = k.[value]
                """
        };

    public bool ContainsKey(string queryKey) => Queries.ContainsKey(queryKey);

    public string GetSql(string queryKey) =>
        Queries.TryGetValue(queryKey, out var sql)
            ? sql
            : throw new KeyNotFoundException($"Unknown QueryKey '{queryKey}'.");
}
