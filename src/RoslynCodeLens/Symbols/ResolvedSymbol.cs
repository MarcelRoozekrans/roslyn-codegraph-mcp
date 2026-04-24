using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Symbols;

public sealed record ResolvedSymbol(ISymbol Symbol, SymbolOrigin Origin);
