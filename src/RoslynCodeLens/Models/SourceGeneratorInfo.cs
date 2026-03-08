namespace RoslynCodeLens.Models;

public record SourceGeneratorInfo(
    string GeneratorName,
    string Project,
    int GeneratedFileCount,
    IReadOnlyList<string> GeneratedFiles);
