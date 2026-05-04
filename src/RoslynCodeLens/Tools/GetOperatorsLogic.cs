using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class GetOperatorsLogic
{
    public static GetOperatorsResult Execute(
        SymbolResolver resolver,
        MetadataSymbolResolver metadata,
        string symbol)
    {
        return new GetOperatorsResult(string.Empty, []);
    }
}
