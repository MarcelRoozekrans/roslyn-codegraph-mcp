namespace RoslynCodeLens.Models;

public record PublicApiEntry(
    PublicApiKind Kind,
    string Name,
    PublicApiAccessibility Accessibility,
    string Project,
    string FilePath,
    int Line);
