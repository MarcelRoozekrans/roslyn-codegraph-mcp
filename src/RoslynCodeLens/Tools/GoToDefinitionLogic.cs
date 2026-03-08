using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class GoToDefinitionLogic
{
    public static IReadOnlyList<SymbolLocation> Execute(SymbolResolver resolver, string symbol)
    {
        var symbols = resolver.FindSymbols(symbol);
        if (symbols.Count == 0)
            return [];

        var results = new List<SymbolLocation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var s in symbols)
        {
            var (file, line) = resolver.GetFileAndLine(s);
            if (string.IsNullOrEmpty(file))
                continue;

            var fullName = s.ToDisplayString();
            if (!seen.Add(fullName))
                continue;

            var kind = s switch
            {
                INamedTypeSymbol t => t.TypeKind switch
                {
                    TypeKind.Interface => "interface",
                    TypeKind.Struct => "struct",
                    TypeKind.Enum => "enum",
                    TypeKind.Delegate => "delegate",
                    _ => "class"
                },
                IMethodSymbol => "method",
                IPropertySymbol => "property",
                IFieldSymbol => "field",
                IEventSymbol => "event",
                _ => "symbol"
            };

            var project = resolver.GetProjectName(s);
            results.Add(new SymbolLocation(kind, fullName, file, line, project, resolver.IsGenerated(file)));
        }

        return results;
    }
}
