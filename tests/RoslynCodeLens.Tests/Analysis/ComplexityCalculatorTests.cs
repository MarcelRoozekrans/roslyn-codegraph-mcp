using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;

namespace RoslynCodeLens.Tests.Analysis;

public class ComplexityCalculatorTests
{
    [Fact]
    public void Calculate_TrivialMethod_ReturnsOne()
    {
        var method = ParseMethod("public void M() { return; }");
        Assert.Equal(1, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Calculate_IfStatement_AddsOne()
    {
        var method = ParseMethod("public void M(bool x) { if (x) return; }");
        Assert.Equal(2, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Calculate_NestedIfElseAndLoop_CountsAll()
    {
        var method = ParseMethod(@"
            public int M(int x)
            {
                if (x > 0)
                {
                    for (int i = 0; i < x; i++)
                    {
                        if (i % 2 == 0) return i;
                    }
                }
                else
                {
                    return -1;
                }
                return 0;
            }");
        // base 1 + if + for + nested if + else = 5
        Assert.Equal(5, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Calculate_BooleanShortCircuit_AddsOnePerOperator()
    {
        var method = ParseMethod("public bool M(bool a, bool b, bool c) { return a && b || c; }");
        // base 1 + && + || = 3
        Assert.Equal(3, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Calculate_AccessorDeclaration_WorksOnAccessor()
    {
        var accessor = ParsePropertyGetter(@"
            public int Total
            {
                get
                {
                    if (_x > 0) return _x;
                    return 0;
                }
            }");
        // base 1 + if = 2
        Assert.Equal(2, ComplexityCalculator.Calculate(accessor));
    }

    private static MethodDeclarationSyntax ParseMethod(string code)
    {
        var tree = CSharpSyntaxTree.ParseText($"class C {{ {code} }}");
        return tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
    }

    private static AccessorDeclarationSyntax ParsePropertyGetter(string code)
    {
        var tree = CSharpSyntaxTree.ParseText($"class C {{ {code} }}");
        return tree.GetRoot().DescendantNodes().OfType<AccessorDeclarationSyntax>().First();
    }
}
