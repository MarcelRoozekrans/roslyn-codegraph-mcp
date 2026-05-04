namespace RoslynCodeLens.Models;

public record OperatorInfo(
    string Kind,
    string Signature,
    string ReturnType,
    IReadOnlyList<OverloadParameter> Parameters,
    string Accessibility,
    bool IsCheckedVariant,
    string? XmlDocSummary,
    string FilePath,
    int Line);
