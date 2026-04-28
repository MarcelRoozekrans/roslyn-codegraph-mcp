namespace RoslynCodeLens.Models;

public record AsyncViolation(
    AsyncViolationPattern Pattern,
    AsyncViolationSeverity Severity,
    string FilePath,
    int Line,
    string ContainingMethod,
    string Project,
    string Snippet);
