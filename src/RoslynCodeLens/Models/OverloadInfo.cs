namespace RoslynCodeLens.Models;

public record OverloadInfo(
    string Signature,
    string ReturnType,
    IReadOnlyList<OverloadParameter> Parameters,
    string Accessibility,
    bool IsStatic,
    bool IsVirtual,
    bool IsAbstract,
    bool IsOverride,
    bool IsAsync,
    bool IsExtensionMethod,
    IReadOnlyList<string> TypeParameters,
    string? XmlDocSummary,
    string FilePath,
    int Line);
