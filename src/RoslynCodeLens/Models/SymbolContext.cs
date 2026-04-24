namespace RoslynCodeLens.Models;

public record SymbolContext(
    string FullName,
    string Namespace,
    string Project,
    string File,
    int Line,
    string? BaseClass,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<string> InjectedDependencies,
    IReadOnlyList<string> PublicMembers,
    SymbolOrigin? Origin = null);
