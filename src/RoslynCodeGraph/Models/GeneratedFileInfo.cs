namespace RoslynCodeGraph.Models;

public record GeneratedFileInfo(
    string FilePath,
    string Project,
    string? GeneratorName,
    IReadOnlyList<string> DefinedTypes,
    string SourceText);
