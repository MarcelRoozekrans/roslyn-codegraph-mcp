using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class GetComplexityMetricsLogic
{
    public static IReadOnlyList<ComplexityMetric> Execute(LoadedSolution loaded, SymbolResolver resolver, string? project, int threshold)
    {
        var results = new List<ComplexityMetric>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var projectName = resolver.GetProjectName(projectId);

            if (project != null &&
                !projectName.Contains(project, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var complexity = ComplexityCalculator.Calculate(method);

                    if (complexity < threshold)
                        continue;

                    var symbol = semanticModel.GetDeclaredSymbol(method);
                    var methodName = symbol?.Name ?? method.Identifier.Text;
                    var typeName = symbol?.ContainingType?.Name ?? "Unknown";
                    var lineSpan = method.GetLocation().GetLineSpan();
                    var file = lineSpan.Path ?? "";
                    var line = lineSpan.StartLinePosition.Line + 1;

                    results.Add(new ComplexityMetric(methodName, typeName, complexity, file, line, projectName));
                }
            }
        }

        return results.OrderByDescending(r => r.Complexity).ToList();
    }
}
