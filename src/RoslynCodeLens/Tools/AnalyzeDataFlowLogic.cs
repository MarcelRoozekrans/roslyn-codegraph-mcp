using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class AnalyzeDataFlowLogic
{
    public static async Task<DataFlowInfo?> ExecuteAsync(
        LoadedSolution loaded, string filePath, int startLine, int endLine, CancellationToken ct)
    {
        var (targetDocument, compilation) = FlowAnalysisHelpers.FindDocument(loaded, filePath);

        if (targetDocument == null || compilation == null)
            return null;

        var syntaxTree = await targetDocument.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
        if (syntaxTree == null) return null;

        var root = await syntaxTree.GetRootAsync(ct).ConfigureAwait(false);
        var text = await syntaxTree.GetTextAsync(ct).ConfigureAwait(false);

        if (startLine < 1 || startLine > text.Lines.Count || endLine < startLine || endLine > text.Lines.Count)
            return null;

        // Convert 1-based lines to text positions
        var startPos = text.Lines[startLine - 1].Start;
        var endPos = text.Lines[endLine - 1].End;

        // Find statements spanning the range
        var statements = root.DescendantNodes()
            .OfType<StatementSyntax>()
            .Where(s => s.SpanStart >= startPos && s.Span.End <= endPos)
            .ToList();

        if (statements.Count == 0)
            return null;

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        DataFlowAnalysis? analysis = null;

        try
        {
            analysis = semanticModel.AnalyzeDataFlow(statements[0], statements[^1]);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (analysis == null || !analysis.Succeeded)
            return null;

        return new DataFlowInfo(
            Declared: analysis.VariablesDeclared.Select(s => s.Name).ToList(),
            Read: analysis.ReadInside.Select(s => s.Name).ToList(),
            Written: analysis.WrittenInside.Select(s => s.Name).ToList(),
            AlwaysAssigned: analysis.AlwaysAssigned.Select(s => s.Name).ToList(),
            Captured: analysis.Captured.Select(s => s.Name).ToList(),
            DataFlowsIn: analysis.DataFlowsIn.Select(s => s.Name).ToList(),
            DataFlowsOut: analysis.DataFlowsOut.Select(s => s.Name).ToList());
    }
}
