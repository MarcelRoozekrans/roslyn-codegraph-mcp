namespace RoslynCodeLens.Models;

public record PublicApiSummary(
    int TotalEntries,
    IReadOnlyDictionary<string, int> ByKind,
    IReadOnlyDictionary<string, int> ByProject,
    IReadOnlyDictionary<string, int> ByAccessibility);
