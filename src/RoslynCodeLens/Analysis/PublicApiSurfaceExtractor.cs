using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Analysis;

/// <summary>
/// Walks an <see cref="IAssemblySymbol"/> and yields every public-or-reachable-protected entry
/// (type and member) for the API surface.
/// </summary>
public static class PublicApiSurfaceExtractor
{
    private static readonly SymbolDisplayFormat ApiMemberFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
                     | SymbolDisplayMemberOptions.IncludeParameters
                     | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <param name="assembly">Assembly to walk.</param>
    /// <param name="projectName">Project name attached to each entry.</param>
    /// <param name="requireSourceLocation">
    /// True for source compilations (skip metadata-only members and members in generated files).
    /// False for baseline DLL extraction (include everything in the assembly).
    /// </param>
    public static IReadOnlyList<PublicApiEntry> Extract(
        IAssemblySymbol assembly,
        string projectName,
        bool requireSourceLocation)
    {
        var entries = new List<PublicApiEntry>();

        foreach (var type in EnumerateTypes(assembly.GlobalNamespace))
        {
            if (!IsApiVisibleType(type, requireSourceLocation)) continue;

            entries.Add(BuildTypeEntry(type, projectName, requireSourceLocation));

            var protectedReachable = type.TypeKind == TypeKind.Class && !type.IsSealed;
            foreach (var member in type.GetMembers())
            {
                if (member is INamedTypeSymbol) continue;
                if (ShouldSkipMember(member)) continue;

                var apiAcc = ClassifyMemberAccessibility(member, protectedReachable);
                if (apiAcc is null) continue;

                if (requireSourceLocation)
                {
                    if (!HasInSourceLocation(member)) continue;
                    if (IsInGeneratedFile(member)) continue;
                }

                var entry = BuildMemberEntry(member, apiAcc.Value, projectName, requireSourceLocation);
                if (entry is not null)
                    entries.Add(entry);
            }
        }

        return entries;
    }

    private static bool IsApiVisibleType(INamedTypeSymbol type, bool requireSourceLocation)
    {
        if (type.IsImplicitlyDeclared) return false;

        if (requireSourceLocation)
        {
            if (!HasInSourceLocation(type)) return false;
            if (IsInGeneratedFile(type)) return false;
        }

        // Every type in the containing-type chain must also be Public.
        // A `public class Nested` inside an `internal class Outer` is NOT externally reachable.
        for (var t = type; t is not null; t = t.ContainingType)
        {
            if (t.DeclaredAccessibility != Accessibility.Public) return false;
        }

        return true;
    }

    private static bool HasInSourceLocation(ISymbol symbol)
        => symbol.Locations.Any(l => l.IsInSource);

    private static bool ShouldSkipMember(ISymbol member)
    {
        // Positional record properties are implicitly declared (synthesized from primary constructor
        // parameters), but ARE part of the public API surface — keep them.
        if (member is IPropertySymbol prop
            && prop.ContainingType is { IsRecord: true }
            && !string.Equals(prop.Name, "EqualityContract", StringComparison.Ordinal))
        {
            return false;
        }

        // Record primary constructors are also implicit but ARE part of the API. Detect them by
        // checking that the first parameter type is NOT the containing type itself (which would
        // mark the synthesized copy constructor we want to drop).
        if (member is IMethodSymbol method
            && method.MethodKind == MethodKind.Constructor
            && method.ContainingType is { IsRecord: true }
            && method.Parameters.Length > 0
            && !SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, method.ContainingType))
        {
            return false;
        }

        return member.IsImplicitlyDeclared;
    }

    private static bool IsInGeneratedFile(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location?.SourceTree is null) return false;
        return GeneratedCodeDetector.IsGenerated(location.SourceTree);
    }

    private static PublicApiAccessibility? ClassifyMemberAccessibility(ISymbol member, bool protectedReachable)
    {
        switch (member.DeclaredAccessibility)
        {
            case Accessibility.Public:
                return PublicApiAccessibility.Public;
            case Accessibility.Protected:
            case Accessibility.ProtectedOrInternal:
                return protectedReachable ? PublicApiAccessibility.Protected : null;
            default:
                return null;
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in EnumerateNestedTypes(type))
                yield return nested;
        }
        foreach (var nested in ns.GetNamespaceMembers())
            foreach (var type in EnumerateTypes(nested))
                yield return type;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in EnumerateNestedTypes(nested))
                yield return deeper;
        }
    }

    private static PublicApiEntry BuildTypeEntry(INamedTypeSymbol type, string projectName, bool requireSourceLocation)
    {
        var (filePath, line) = LocationFor(type, requireSourceLocation);

        return new PublicApiEntry(
            Kind: TypeKindToApiKind(type),
            Name: FullyQualified(type),
            Accessibility: PublicApiAccessibility.Public,
            Project: projectName,
            FilePath: filePath,
            Line: line);
    }

    private static PublicApiKind TypeKindToApiKind(INamedTypeSymbol type)
    {
        if (type.IsRecord)
        {
            return type.TypeKind == TypeKind.Struct ? PublicApiKind.RecordStruct : PublicApiKind.Record;
        }

        return type.TypeKind switch
        {
            TypeKind.Class => PublicApiKind.Class,
            TypeKind.Struct => PublicApiKind.Struct,
            TypeKind.Interface => PublicApiKind.Interface,
            TypeKind.Enum => PublicApiKind.Enum,
            TypeKind.Delegate => PublicApiKind.Delegate,
            _ => PublicApiKind.Class
        };
    }

    private static PublicApiEntry? BuildMemberEntry(ISymbol member, PublicApiAccessibility apiAcc, string projectName, bool requireSourceLocation)
    {
        var (kind, name) = MemberKindAndName(member);
        if (kind is null) return null;

        var (filePath, line) = LocationFor(member, requireSourceLocation);
        return new PublicApiEntry(
            Kind: kind.Value,
            Name: name,
            Accessibility: apiAcc,
            Project: projectName,
            FilePath: filePath,
            Line: line);
    }

    private static (string FilePath, int Line) LocationFor(ISymbol symbol, bool requireSourceLocation)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is not null)
        {
            var lineSpan = location.GetLineSpan();
            return (lineSpan.Path, lineSpan.StartLinePosition.Line + 1);
        }

        if (requireSourceLocation)
        {
            // Should not happen — IsApiVisibleType already filtered metadata-only types away.
            return (string.Empty, 0);
        }

        // Metadata symbol (baseline DLL path) — no source location available.
        return (string.Empty, 0);
    }

    private static (PublicApiKind? Kind, string Name) MemberKindAndName(ISymbol member)
    {
        var memberName = MemberDisplayName(member);

        switch (member)
        {
            case IMethodSymbol method:
                return method.MethodKind switch
                {
                    MethodKind.Constructor => (PublicApiKind.Constructor, memberName),
                    MethodKind.UserDefinedOperator => (PublicApiKind.Operator, memberName),
                    MethodKind.Conversion => (PublicApiKind.Operator, memberName),
                    MethodKind.Ordinary => (PublicApiKind.Method, memberName),
                    MethodKind.PropertyGet or MethodKind.PropertySet
                        or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise
                        or MethodKind.Destructor
                        or MethodKind.StaticConstructor => (null, memberName),
                    _ => (null, memberName)
                };

            case IPropertySymbol property:
                return property.IsIndexer
                    ? (PublicApiKind.Indexer, memberName)
                    : (PublicApiKind.Property, memberName);

            case IFieldSymbol:
                return (PublicApiKind.Field, memberName);

            case IEventSymbol:
                return (PublicApiKind.Event, memberName);

            default:
                return (null, memberName);
        }
    }

    private static string MemberDisplayName(ISymbol member)
        => member.ToDisplayString(ApiMemberFormat);

    private static string FullyQualified(ISymbol symbol)
        => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal);
}
