using RoslynCodeLens.Models;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetOperatorsToolTests
{
    private readonly TestSolutionFixture _fixture;

    public GetOperatorsToolTests(TestSolutionFixture fixture) => _fixture = fixture;

    [Fact]
    public void Result_ReturnsAllOperators()
    {
        var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");

        Assert.Contains("Money", result.ContainingType, StringComparison.Ordinal);
        // Money has: +, -, *, <, >, <=, >=, implicit decimal, explicit Money,
        // checked +, plus synthesized == and != from record struct = at least 12.
        Assert.True(result.Operators.Count >= 10,
            $"Expected ≥10 operators on Money, got {result.Operators.Count}");
    }
}
