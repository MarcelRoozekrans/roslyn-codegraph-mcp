namespace RoslynCodeLens.Models;

public record FileOverview(
    string FilePath,
    string? Project,
    IReadOnlyList<string> TypesDefined,
    IReadOnlyList<DiagnosticInfo> Diagnostics);
