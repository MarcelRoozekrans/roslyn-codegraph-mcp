namespace RoslynCodeLens.Models;

public record FindUncoveredSymbolsResult(
    CoverageSummary Summary,
    IReadOnlyList<UncoveredSymbol> UncoveredSymbols);
