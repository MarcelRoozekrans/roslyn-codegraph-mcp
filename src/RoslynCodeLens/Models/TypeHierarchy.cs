namespace RoslynCodeLens.Models;

public record TypeHierarchy(
    IReadOnlyList<SymbolLocation> Bases,
    IReadOnlyList<SymbolLocation> Interfaces,
    IReadOnlyList<SymbolLocation> Derived,
    SymbolOrigin? Origin = null);
