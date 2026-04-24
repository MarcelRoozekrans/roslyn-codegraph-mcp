namespace RoslynCodeLens.Models;

public record SymbolOrigin(
    string Kind,                 // "source" | "metadata"
    string? AssemblyName,        // null when Kind=="source"
    string? AssemblyVersion,     // null when Kind=="source"
    string? DocId);              // null when Kind=="source"; set for metadata
