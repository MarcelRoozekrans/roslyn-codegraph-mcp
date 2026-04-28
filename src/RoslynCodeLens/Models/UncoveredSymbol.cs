namespace RoslynCodeLens.Models;

public record UncoveredSymbol(
    string Symbol,
    UncoveredSymbolKind Kind,
    string FilePath,
    int Line,
    string Project,
    int Complexity);
