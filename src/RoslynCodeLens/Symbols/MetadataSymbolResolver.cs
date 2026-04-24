using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Symbols;

public sealed class MetadataSymbolResolver
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _source;

    public MetadataSymbolResolver(LoadedSolution loaded, SymbolResolver source)
    {
        _loaded = loaded;
        _source = source;
    }

    public ResolvedSymbol? Resolve(string name)
    {
        // 1. Source first — matches how the compilation binds. The source resolver's
        //    indexes are built from Compilation.GlobalNamespace, which is a merged view
        //    including referenced metadata, so a hit here may still be a metadata symbol;
        //    ToOrigin inspects Locations to classify accurately.
        var sourceMatches = _source.FindSymbols(name);
        foreach (var match in sourceMatches)
        {
            if (match.Locations.Any(l => l.IsInSource))
                return new ResolvedSymbol(match, SourceOrigin);
        }

        if (sourceMatches.Count > 0)
        {
            var first = sourceMatches[0];
            return new ResolvedSymbol(first, ToOrigin(first));
        }

        // 2. Metadata fallback via Compilation.GetTypeByMetadataName (handles names the
        //    source index does not reach, e.g. when SymbolResolver's display-string key
        //    differs from the metadata name).
        foreach (var compilation in _loaded.Compilations.Values)
        {
            var type = compilation.GetTypeByMetadataName(name);
            if (type != null && type.Locations.All(l => !l.IsInSource))
                return new ResolvedSymbol(type, ToOrigin(type));

            var lastDot = name.LastIndexOf('.');
            if (lastDot <= 0)
                continue;

            var typeName = name[..lastDot];
            var memberName = name[(lastDot + 1)..];
            var container = compilation.GetTypeByMetadataName(typeName);
            if (container == null || container.Locations.Any(l => l.IsInSource))
                continue;

            var member = FindMemberIncludingInherited(container, memberName);
            if (member != null)
                return new ResolvedSymbol(member, ToOrigin(member));
        }

        return null;
    }

    private static ISymbol? FindMemberIncludingInherited(INamedTypeSymbol container, string memberName)
    {
        var direct = container.GetMembers(memberName);
        if (direct.Length > 0)
            return direct[0];

        // For interfaces, search AllInterfaces for the member. For classes/structs,
        // walk the base chain. Matches how the compiler resolves member-access on
        // the static type.
        if (container.TypeKind == TypeKind.Interface)
        {
            foreach (var iface in container.AllInterfaces)
            {
                var members = iface.GetMembers(memberName);
                if (members.Length > 0)
                    return members[0];
            }
        }
        else
        {
            var current = container.BaseType;
            while (current != null)
            {
                var members = current.GetMembers(memberName);
                if (members.Length > 0)
                    return members[0];
                current = current.BaseType;
            }
        }

        return null;
    }

    public IEnumerable<IAssemblySymbol> EnumerateMetadataAssemblies()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var compilation in _loaded.Compilations.Values)
        {
            foreach (var asm in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                if (seen.Add(asm.Identity.GetDisplayName()))
                    yield return asm;
            }
        }
    }

    public IAssemblySymbol? FindAssembly(string assemblyName)
    {
        foreach (var asm in EnumerateMetadataAssemblies())
        {
            if (string.Equals(asm.Identity.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                return asm;
        }
        return null;
    }

    public static SymbolOrigin ToOrigin(ISymbol symbol)
    {
        if (symbol.Locations.Any(l => l.IsInSource))
            return SourceOrigin;

        var asm = symbol.ContainingAssembly;
        return new SymbolOrigin(
            "metadata",
            asm?.Identity.Name,
            asm?.Identity.Version.ToString(),
            symbol.GetDocumentationCommentId());
    }

    public static SymbolOrigin SourceOrigin { get; } = new("source", null, null, null);
}
