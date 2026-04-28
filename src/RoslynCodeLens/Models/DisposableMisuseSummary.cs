namespace RoslynCodeLens.Models;

public record DisposableMisuseSummary(
    int TotalViolations,
    IReadOnlyDictionary<string, int> ByPattern,
    IReadOnlyDictionary<string, int> BySeverity);
