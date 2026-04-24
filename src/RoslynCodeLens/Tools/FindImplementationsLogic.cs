using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class FindImplementationsLogic
{
    public static IReadOnlyList<SymbolLocation> Execute(
        LoadedSolution loaded, SymbolResolver source, MetadataSymbolResolver metadata, string symbol)
    {
        var targetTypes = source.FindNamedTypes(symbol);
        if (targetTypes.Count == 0)
        {
            var resolved = metadata.Resolve(symbol);
            if (resolved?.Symbol is INamedTypeSymbol nt)
                targetTypes = [nt];
            else
                return [];
        }

        var results = new List<SymbolLocation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var target in targetTypes)
        {
            IReadOnlyList<INamedTypeSymbol> candidates;

            if (target.TypeKind == TypeKind.Interface)
                candidates = source.GetInterfaceImplementors(target);
            else
                candidates = source.GetDerivedTypes(target);

            foreach (var candidate in candidates)
            {
                var fullName = candidate.ToDisplayString();
                if (!seen.Add(fullName))
                    continue;

                var (file, line) = source.GetFileAndLine(candidate);
                var project = source.GetProjectName(candidate);
                var kind = candidate.TypeKind switch
                {
                    TypeKind.Struct => "struct",
                    TypeKind.Interface => "interface",
                    _ => "class"
                };
                results.Add(new SymbolLocation(kind, fullName, file, line, project, source.IsGenerated(file)));
            }
        }

        return results;
    }
}
