using Microsoft.CodeAnalysis;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetTypeHierarchyLogic
{
    public static TypeHierarchy? Execute(LoadedSolution loaded, SymbolResolver resolver, string symbol)
    {
        var types = resolver.FindNamedTypes(symbol);
        if (types.Count == 0)
            return null;

        var target = types[0];

        var bases = CollectBaseTypes(target, resolver);
        var interfaces = CollectInterfaces(target, resolver);
        var derived = CollectDerivedTypes(target, resolver);

        return new TypeHierarchy(bases, interfaces, derived);
    }

    private static List<SymbolLocation> CollectBaseTypes(INamedTypeSymbol target, SymbolResolver resolver)
    {
        var bases = new List<SymbolLocation>();
        var baseType = target.BaseType;
        while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
        {
            var (file, line) = resolver.GetFileAndLine(baseType);
            var project = resolver.GetProjectName(baseType);
            var kind = GetTypeKindString(baseType);
            bases.Add(new SymbolLocation(kind, baseType.ToDisplayString(), file, line, project));
            baseType = baseType.BaseType;
        }
        return bases;
    }

    private static List<SymbolLocation> CollectInterfaces(INamedTypeSymbol target, SymbolResolver resolver)
    {
        var interfaces = new List<SymbolLocation>();
        foreach (var iface in target.AllInterfaces)
        {
            var (file, line) = resolver.GetFileAndLine(iface);
            var project = resolver.GetProjectName(iface);
            interfaces.Add(new SymbolLocation("interface", iface.ToDisplayString(), file, line, project));
        }
        return interfaces;
    }

    private static List<SymbolLocation> CollectDerivedTypes(INamedTypeSymbol target, SymbolResolver resolver)
    {
        var derived = new List<SymbolLocation>();
        var derivedTypes = target.TypeKind == TypeKind.Interface
            ? resolver.GetInterfaceImplementors(target)
            : resolver.GetDerivedTypes(target);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in derivedTypes)
        {
            var fullName = candidate.ToDisplayString();
            if (!seen.Add(fullName))
                continue;

            var (file, line) = resolver.GetFileAndLine(candidate);
            var project = resolver.GetProjectName(candidate);
            var kind = GetTypeKindString(candidate);
            derived.Add(new SymbolLocation(kind, fullName, file, line, project));
        }
        return derived;
    }

    private static string GetTypeKindString(INamedTypeSymbol type)
    {
        return type.TypeKind switch
        {
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            _ => "class"
        };
    }
}
