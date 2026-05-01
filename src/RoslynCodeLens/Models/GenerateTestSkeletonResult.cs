namespace RoslynCodeLens.Models;

public record GenerateTestSkeletonResult(
    string Framework,
    string SuggestedFilePath,
    string ClassName,
    string Code,
    IReadOnlyList<string> TodoNotes);
