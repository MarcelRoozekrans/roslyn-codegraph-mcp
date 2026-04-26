using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class FindTestsForSymbolLogic
{
    public static FindTestsForSymbolResult Execute(
        LoadedSolution loaded,
        SymbolResolver source,
        MetadataSymbolResolver metadata,
        string symbol,
        bool transitive = false,
        int maxDepth = 3)
    {
        // Clamp maxDepth into [1, 5]
        maxDepth = Math.Clamp(maxDepth, 1, 5);

        var targetMethods = source.FindMethods(symbol);
        if (targetMethods.Count == 0)
            return new FindTestsForSymbolResult(symbol, [], []);

        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);
        if (testProjectIds.IsEmpty)
            return new FindTestsForSymbolResult(symbol, [], []);

        var directTests = new List<TestReference>();
        var seenTestSymbols = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var targetSet = new HashSet<IMethodSymbol>(targetMethods, SymbolEqualityComparer.Default);

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            if (!testProjectIds.Contains(projectId))
                continue;

            var projectName = source.GetProjectName(projectId);

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    if (symbolInfo.Symbol is not IMethodSymbol calledMethod)
                        continue;

                    if (!targetSet.Contains(calledMethod) && !targetSet.Contains(calledMethod.OriginalDefinition))
                        continue;

                    // Find the enclosing method symbol
                    var enclosingMethod = FindEnclosingMethodSymbol(invocation, semanticModel);
                    if (enclosingMethod is null)
                        continue;

                    if (!seenTestSymbols.Add(enclosingMethod))
                        continue;

                    var testInfo = ClassifyAsTest(enclosingMethod, projectName);
                    if (testInfo is not null)
                        directTests.Add(testInfo);
                }
            }
        }

        return new FindTestsForSymbolResult(symbol, directTests, []);
    }

    private static IMethodSymbol? FindEnclosingMethodSymbol(SyntaxNode node, SemanticModel semanticModel)
    {
        var methodDecl = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (methodDecl is null)
            return null;
        return semanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
    }

    private static TestReference? ClassifyAsTest(IMethodSymbol method, string projectName)
    {
        foreach (var attr in method.GetAttributes())
        {
            var ns = attr.AttributeClass?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            var name = attr.AttributeClass?.Name ?? string.Empty;

            var framework = TestAttributeRecognizer.Recognize(ns, name);
            if (framework is not null)
            {
                var location = method.Locations.FirstOrDefault(l => l.IsInSource);
                if (location is null)
                    return null;

                var lineSpan = location.GetLineSpan();
                var attributeShortName = name.EndsWith("Attribute", StringComparison.Ordinal)
                    ? name[..^"Attribute".Length]
                    : name;

                return new TestReference(
                    FullyQualifiedName: method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal),
                    Framework: framework.Value,
                    Attribute: attributeShortName,
                    FilePath: lineSpan.Path,
                    Line: lineSpan.StartLinePosition.Line + 1,
                    Project: projectName);
            }
        }

        return null;
    }
}
