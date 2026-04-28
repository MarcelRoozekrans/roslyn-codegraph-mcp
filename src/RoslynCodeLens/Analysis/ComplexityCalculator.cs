using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynCodeLens.Analysis;

public static class ComplexityCalculator
{
    /// <summary>
    /// Computes McCabe cyclomatic complexity for the given syntax node.
    /// Counts: if/else, switch sections, for/foreach/while/do, catch, conditional expression,
    /// short-circuit operators (&&, ||, ??).
    /// </summary>
    public static int Calculate(SyntaxNode node)
    {
        var complexity = 1;

        foreach (var descendant in node.DescendantNodes())
        {
            switch (descendant.Kind())
            {
                case SyntaxKind.IfStatement:
                case SyntaxKind.ElseClause:
                case SyntaxKind.SwitchSection:
                case SyntaxKind.ForStatement:
                case SyntaxKind.ForEachStatement:
                case SyntaxKind.WhileStatement:
                case SyntaxKind.DoStatement:
                case SyntaxKind.CatchClause:
                case SyntaxKind.ConditionalExpression:
                    complexity++;
                    break;
            }
        }

        foreach (var token in node.DescendantTokens())
        {
#pragma warning disable EPS06
            var kind = token.Kind();
#pragma warning restore EPS06
            switch (kind)
            {
                case SyntaxKind.AmpersandAmpersandToken:
                case SyntaxKind.BarBarToken:
                case SyntaxKind.QuestionQuestionToken:
                    complexity++;
                    break;
            }
        }

        return complexity;
    }
}
