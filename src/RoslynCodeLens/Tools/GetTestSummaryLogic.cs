using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class GetTestSummaryLogic
{
    private static readonly SymbolDisplayFormat FqnFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType);

    public static GetTestSummaryResult Execute(
        LoadedSolution loaded, SymbolResolver resolver, string? project)
    {
        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);
        var entries = new List<ProjectTestSummary>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            if (!testProjectIds.Contains(projectId)) continue;

            var projectName = resolver.GetProjectName(projectId);
            if (project is not null && !string.Equals(projectName, project, StringComparison.OrdinalIgnoreCase))
                continue;

            var tests = new List<TestMethodSummary>();

            foreach (var type in EnumerateTypes(compilation.Assembly.GlobalNamespace))
            {
                if (!type.Locations.Any(l => l.IsInSource)) continue;

                foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
                {
                    var classification = TestMethodClassifier.Classify(member);
                    if (classification is null) continue;

                    var (file, line) = GetFileAndLine(member);
                    if (string.IsNullOrEmpty(file)) continue;

                    var rowCount = CountInlineDataRows(member, classification.Framework);
                    var referenced = CollectReferencedSymbols(member, compilation);

                    tests.Add(new TestMethodSummary(
                        MethodName: member.ToDisplayString(FqnFormat).Replace("global::", string.Empty, StringComparison.Ordinal),
                        Framework: classification.Framework.ToString(),
                        AttributeShortName: classification.AttributeShortName,
                        InlineDataRowCount: rowCount,
                        ReferencedSymbols: referenced,
                        FilePath: file,
                        Line: line));
                }
            }

            tests.Sort((a, b) =>
            {
                var fileCmp = string.CompareOrdinal(a.FilePath, b.FilePath);
                return fileCmp != 0 ? fileCmp : a.Line.CompareTo(b.Line);
            });

            var byFramework = tests
                .GroupBy(t => t.Framework, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var byAttribute = tests
                .GroupBy(t => t.AttributeShortName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            entries.Add(new ProjectTestSummary(
                Project: projectName,
                TotalTests: tests.Count,
                ByFramework: byFramework,
                ByAttribute: byAttribute,
                Tests: tests));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Project, b.Project));
        return new GetTestSummaryResult(entries);
    }

    private static int CountInlineDataRows(IMethodSymbol method, TestFramework framework)
    {
        var dataAttributeName = framework switch
        {
            TestFramework.XUnit => "InlineDataAttribute",
            TestFramework.NUnit => "TestCaseAttribute",
            TestFramework.MSTest => "DataRowAttribute",
            _ => null,
        };
        if (dataAttributeName is null) return 0;

        return method.GetAttributes().Count(a =>
            string.Equals(a.AttributeClass?.Name, dataAttributeName, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> CollectReferencedSymbols(IMethodSymbol method, Compilation compilation)
    {
        var location = method.Locations.FirstOrDefault(l => l.IsInSource);
        if (location?.SourceTree is null) return [];

        var semanticModel = compilation.GetSemanticModel(location.SourceTree);
        var bodyNode = location.SourceTree.GetRoot().FindNode(location.SourceSpan);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in bodyNode.DescendantNodesAndSelf())
        {
            var symbol = semanticModel.GetSymbolInfo(node).Symbol;
            if (symbol is null) continue;
            if (symbol is not (IMethodSymbol or IPropertySymbol or IFieldSymbol)) continue;

            var ns = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            // Containing-type's namespace for members:
            if (symbol is not INamedTypeSymbol)
                ns = symbol.ContainingType?.ContainingNamespace?.ToDisplayString() ?? ns;

            if (IsExcludedNamespace(ns)) continue;

            var fqn = symbol.ToDisplayString(FqnFormat).Replace("global::", string.Empty, StringComparison.Ordinal);
            seen.Add(fqn);
        }

        return seen.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static bool IsExcludedNamespace(string ns)
    {
        if (string.IsNullOrEmpty(ns)) return false;
        return ns == "Xunit" || ns.StartsWith("Xunit.", StringComparison.Ordinal)
            || ns == "NUnit.Framework" || ns.StartsWith("NUnit.Framework.", StringComparison.Ordinal)
            || ns == "Microsoft.VisualStudio.TestTools.UnitTesting" || ns.StartsWith("Microsoft.VisualStudio.TestTools.UnitTesting.", StringComparison.Ordinal)
            || ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)
            || ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal);
    }

    private static (string File, int Line) GetFileAndLine(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null) return (string.Empty, 0);
        var span = location.GetLineSpan();
        return (span.Path, span.StartLinePosition.Line + 1);
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
}
