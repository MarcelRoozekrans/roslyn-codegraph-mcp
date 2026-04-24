namespace RoslynCodeLens.Models;

public record ExternalMemberInfo(
    string Kind,                   // "method" | "property" | "field" | "event" | "constructor"
    string Signature,
    string? XmlDocSummary);
