namespace RoslynCodeLens.Models;

public record BreakingChangesSummary(
    int TotalChanges,
    IReadOnlyDictionary<string, int> ByKind,
    IReadOnlyDictionary<string, int> BySeverity);
