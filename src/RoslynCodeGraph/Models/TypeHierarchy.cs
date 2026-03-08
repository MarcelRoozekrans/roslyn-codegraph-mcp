namespace RoslynCodeGraph.Models;

public record TypeHierarchy(
    IReadOnlyList<SymbolLocation> Bases,
    IReadOnlyList<SymbolLocation> Interfaces,
    IReadOnlyList<SymbolLocation> Derived);
