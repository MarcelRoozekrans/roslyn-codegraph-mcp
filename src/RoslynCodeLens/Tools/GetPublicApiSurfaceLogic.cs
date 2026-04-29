using Microsoft.CodeAnalysis;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class GetPublicApiSurfaceLogic
{
    public static GetPublicApiSurfaceResult Execute(LoadedSolution loaded, SymbolResolver source)
    {
        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);
        var entries = new List<PublicApiEntry>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            if (testProjectIds.Contains(projectId)) continue;

            var projectName = source.GetProjectName(projectId);

            foreach (var type in EnumerateTypes(compilation.Assembly.GlobalNamespace))
            {
                if (!IsApiVisibleType(type)) continue;

                entries.Add(BuildTypeEntry(type, projectName));

                var protectedReachable = type.TypeKind == TypeKind.Class && !type.IsSealed;
                foreach (var member in type.GetMembers())
                {
                    if (member is INamedTypeSymbol) continue;
                    if (ShouldSkipMember(member)) continue;

                    var apiAcc = ClassifyMemberAccessibility(member, protectedReachable);
                    if (apiAcc is null) continue;

                    if (!HasInSourceLocation(member)) continue;
                    if (IsInGeneratedFile(member)) continue;

                    var entry = BuildMemberEntry(member, apiAcc.Value, projectName);
                    if (entry is not null)
                        entries.Add(entry);
                }
            }
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var byKind = entries
            .GroupBy(e => e.Kind.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byProject = entries
            .GroupBy(e => e.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byAccessibility = entries
            .GroupBy(e => e.Accessibility.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var summary = new PublicApiSummary(
            TotalEntries: entries.Count,
            ByKind: byKind,
            ByProject: byProject,
            ByAccessibility: byAccessibility);

        return new GetPublicApiSurfaceResult(summary, entries);
    }

    private static bool IsApiVisibleType(INamedTypeSymbol type)
    {
        if (type.IsImplicitlyDeclared) return false;
        if (type.DeclaredAccessibility != Accessibility.Public) return false;
        if (!HasInSourceLocation(type)) return false;
        if (IsInGeneratedFile(type)) return false;
        return true;
    }

    private static bool HasInSourceLocation(ISymbol symbol)
        => symbol.Locations.Any(l => l.IsInSource);

    private static bool ShouldSkipMember(ISymbol member)
    {
        // Positional record properties are implicitly declared (synthesized from the primary constructor
        // parameters), but ARE part of the public API surface — keep them.
        if (member is IPropertySymbol prop
            && prop.ContainingType is { IsRecord: true }
            && !string.Equals(prop.Name, "EqualityContract", StringComparison.Ordinal))
        {
            return false;
        }

        // Record primary constructors are also implicit but ARE part of the API. Detect them by checking
        // for a parameter list whose first parameter type is NOT the containing type itself (which would
        // mark the synthesized copy constructor we want to drop).
        if (member is IMethodSymbol method
            && method.MethodKind == MethodKind.Constructor
            && method.ContainingType is { IsRecord: true }
            && method.Parameters.Length > 0
            && !SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, method.ContainingType))
        {
            return false;
        }

        // Skip every other implicitly declared member (synthesized parameterless constructors,
        // EqualityContract, record-synthesized Equals/GetHashCode/copy ctor/<Clone>$/op_Equality, ...).
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

    private static PublicApiEntry BuildTypeEntry(INamedTypeSymbol type, string projectName)
    {
        var location = type.Locations.First(l => l.IsInSource);
        var lineSpan = location.GetLineSpan();

        return new PublicApiEntry(
            Kind: TypeKindToApiKind(type),
            Name: FullyQualified(type),
            Accessibility: PublicApiAccessibility.Public,
            Project: projectName,
            FilePath: lineSpan.Path,
            Line: lineSpan.StartLinePosition.Line + 1);
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

    private static PublicApiEntry? BuildMemberEntry(ISymbol member, PublicApiAccessibility apiAcc, string projectName)
    {
        var location = member.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null) return null;

        var (kind, name) = MemberKindAndName(member);
        if (kind is null) return null;

        var lineSpan = location.GetLineSpan();
        return new PublicApiEntry(
            Kind: kind.Value,
            Name: name,
            Accessibility: apiAcc,
            Project: projectName,
            FilePath: lineSpan.Path,
            Line: lineSpan.StartLinePosition.Line + 1);
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
    {
        // Build "<ContainingType FQN>.<member name>" so the rendered name is independent of method
        // signatures, indexer brackets, and operator symbols. Constructors render as "<Type>.<Type>".
        var containingFqn = member.ContainingType is null
            ? string.Empty
            : FullyQualified(member.ContainingType);

        var localName = member switch
        {
            IMethodSymbol method when method.MethodKind == MethodKind.Constructor
                => method.ContainingType?.Name ?? method.Name,
            _ => member.Name,
        };

        return string.IsNullOrEmpty(containingFqn) ? localName : $"{containingFqn}.{localName}";
    }

    private static string FullyQualified(ISymbol symbol)
        => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal);
}
