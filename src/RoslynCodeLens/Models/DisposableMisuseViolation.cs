namespace RoslynCodeLens.Models;

public record DisposableMisuseViolation(
    DisposableMisusePattern Pattern,
    DisposableMisuseSeverity Severity,
    string FilePath,
    int Line,
    string ContainingMethod,
    string Project,
    string Snippet);
