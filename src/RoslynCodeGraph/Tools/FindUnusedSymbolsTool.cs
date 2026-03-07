using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindUnusedSymbolsLogic
{
    public static List<UnusedSymbolInfo> Execute(LoadedSolution loaded, SymbolResolver resolver, string? project, bool includeInternal)
    {
        var results = new List<UnusedSymbolInfo>();

        foreach (var type in resolver.AllTypes)
        {
            // Skip types without source locations (framework types)
            if (!type.Locations.Any(l => l.IsInSource))
                continue;

            var projectName = resolver.GetProjectName(type);

            // Apply project filter
            if (project != null &&
                !projectName.Equals(project, StringComparison.OrdinalIgnoreCase))
                continue;

            // Skip types based on accessibility and kind
            if (ShouldSkipType(type, includeInternal))
                continue;

            // Check if the type has any references
            var typeRefs = FindReferencesLogic.Execute(loaded, resolver, type.ToDisplayString());
            if (typeRefs.Count == 0)
            {
                // Type is unused - report it and skip its members
                var (file, line) = resolver.GetFileAndLine(type);
                results.Add(new UnusedSymbolInfo(type.ToDisplayString(), type.TypeKind.ToString(), file, line, projectName));
                continue;
            }

            // Type is referenced - check its public members
            foreach (var member in type.GetMembers())
            {
                if (ShouldSkipMember(member, type, includeInternal))
                    continue;

                var memberSymbol = $"{type.ToDisplayString()}.{member.Name}";
                var memberRefs = FindReferencesLogic.Execute(loaded, resolver, memberSymbol);
                if (memberRefs.Count == 0)
                {
                    var (file, line) = resolver.GetFileAndLine(member);
                    var kind = member switch
                    {
                        IMethodSymbol => "Method",
                        IPropertySymbol => "Property",
                        IFieldSymbol => "Field",
                        IEventSymbol => "Event",
                        _ => member.Kind.ToString()
                    };
                    results.Add(new UnusedSymbolInfo(memberSymbol, kind, file, line, projectName));
                }
            }
        }

        return results;
    }

    private static bool ShouldSkipType(INamedTypeSymbol type, bool includeInternal)
    {
        // Skip private or protected
        if (type.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
            return true;

        // Skip internal unless includeInternal
        if (!includeInternal && type.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal)
            return true;

        // Skip interfaces
        if (type.TypeKind == TypeKind.Interface)
            return true;

        // Skip static classes with extension methods (likely DI setup)
        if (type.IsStatic)
        {
            var hasExtensionMethods = type.GetMembers()
                .OfType<IMethodSymbol>()
                .Any(m => m.IsExtensionMethod);
            if (hasExtensionMethods)
                return true;
        }

        // Skip types containing a "Main" method (entry points)
        if (type.GetMembers("Main").Any())
            return true;

        return false;
    }

    private static bool ShouldSkipMember(ISymbol member, INamedTypeSymbol containingType, bool includeInternal)
    {
        // Skip implicitly declared
        if (member.IsImplicitlyDeclared)
            return true;

        // Skip private or protected
        if (member.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
            return true;

        // Skip internal unless includeInternal
        if (!includeInternal && member.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal)
            return true;

        // Skip non-ordinary methods (property accessors, event accessors, constructors, etc.)
        if (member is IMethodSymbol method)
        {
            if (method.MethodKind != MethodKind.Ordinary)
                return true;

            // Skip override methods
            if (method.IsOverride)
                return true;

            // Skip interface implementation members
            foreach (var iface in containingType.AllInterfaces)
            {
                var impl = containingType.FindImplementationForInterfaceMember(method);
                if (impl != null && SymbolEqualityComparer.Default.Equals(impl, method))
                    return true;
            }
        }

        // Skip override properties
        if (member is IPropertySymbol prop)
        {
            if (prop.IsOverride)
                return true;

            // Skip interface implementation properties
            foreach (var iface in containingType.AllInterfaces)
            {
                var impl = containingType.FindImplementationForInterfaceMember(prop);
                if (impl != null && SymbolEqualityComparer.Default.Equals(impl, prop))
                    return true;
            }
        }

        return false;
    }
}

[McpServerToolType]
public static class FindUnusedSymbolsTool
{
    [McpServerTool(Name = "find_unused_symbols"),
     Description("Find potentially unused types and members (dead code detection). Checks public symbols for references across the solution.")]
    public static List<UnusedSymbolInfo> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Optional project name filter")] string? project = null,
        [Description("Include internal symbols (default: false)")] bool includeInternal = false)
    {
        SolutionGuard.EnsureLoaded(loaded);
        return FindUnusedSymbolsLogic.Execute(loaded, resolver, project, includeInternal);
    }
}
