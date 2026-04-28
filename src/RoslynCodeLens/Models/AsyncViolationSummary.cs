namespace RoslynCodeLens.Models;

public record AsyncViolationSummary(
    int TotalViolations,
    IReadOnlyDictionary<string, int> ByPattern,
    IReadOnlyDictionary<string, int> BySeverity);
