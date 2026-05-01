namespace RoslynCodeLens.Models;

public record TestMethodSummary(
    string MethodName,
    string Framework,
    string AttributeShortName,
    int InlineDataRowCount,
    IReadOnlyList<string> ReferencedSymbols,
    string FilePath,
    int Line);
