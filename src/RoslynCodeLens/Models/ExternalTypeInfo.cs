namespace RoslynCodeLens.Models;

public record ExternalTypeInfo(
    string Kind,                   // "class" | "interface" | "struct" | "enum" | "delegate"
    string FullName,
    IReadOnlyList<string> Modifiers,
    string? BaseType,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<ExternalMemberInfo> Members,
    IReadOnlyList<string> Attributes,
    string? XmlDocSummary);
