namespace RoslynCodeLens.Models;

public record BreakingChange(
    BreakingChangeKind Kind,
    BreakingChangeSeverity Severity,
    string Name,
    PublicApiKind EntityKind,
    string Project,
    string FilePath,
    int Line,
    string Details);
