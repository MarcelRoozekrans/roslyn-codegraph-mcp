using Microsoft.CodeAnalysis;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindImplementationsLogic
{
    public static IReadOnlyList<SymbolLocation> Execute(LoadedSolution loaded, SymbolResolver resolver, string symbol)
    {
        var targetTypes = resolver.FindNamedTypes(symbol);
        var results = new List<SymbolLocation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var target in targetTypes)
        {
            IReadOnlyList<INamedTypeSymbol> candidates;

            if (target.TypeKind == TypeKind.Interface)
                candidates = resolver.GetInterfaceImplementors(target);
            else
                candidates = resolver.GetDerivedTypes(target);

            foreach (var candidate in candidates)
            {
                var fullName = candidate.ToDisplayString();
                if (!seen.Add(fullName))
                    continue;

                var (file, line) = resolver.GetFileAndLine(candidate);
                var project = resolver.GetProjectName(candidate);
                var kind = candidate.TypeKind switch
                {
                    TypeKind.Struct => "struct",
                    TypeKind.Interface => "interface",
                    _ => "class"
                };
                results.Add(new SymbolLocation(kind, fullName, file, line, project, resolver.IsGenerated(file)));
            }
        }

        return results;
    }
}
