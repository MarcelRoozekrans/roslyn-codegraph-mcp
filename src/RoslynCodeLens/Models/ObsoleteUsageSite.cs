namespace RoslynCodeLens.Models;

public record ObsoleteUsageSite(
    string CallerName,
    string FilePath,
    int Line,
    string Snippet,
    string Project,
    bool IsGenerated);
