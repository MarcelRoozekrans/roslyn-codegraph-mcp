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

    [Fact]
    public void Result_ContainingTypeIsFullyQualified()
    {
        var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");
        Assert.Equal("TestLib.Money", result.ContainingType);
    }

    [Fact]
    public void Result_SortedByKindThenArityThenSignature()
    {
        var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");

        for (int i = 1; i < result.Operators.Count; i++)
        {
            var prev = result.Operators[i - 1];
            var curr = result.Operators[i];
            var kindCmp = string.CompareOrdinal(prev.Kind, curr.Kind);
            if (kindCmp == 0)
            {
                var arityCmp = prev.Parameters.Count.CompareTo(curr.Parameters.Count);
                if (arityCmp == 0)
                    Assert.True(string.CompareOrdinal(prev.Signature, curr.Signature) <= 0);
                else
                    Assert.True(arityCmp < 0);
            }
            else
            {
                Assert.True(kindCmp < 0);
            }
        }
    }

    [Fact]
    public void BinaryAddition_KindIsPlus()
    {
        var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");
        Assert.Contains(result.Operators, op => op.Kind == "+" && !op.IsCheckedVariant);
    }

    [Fact]
    public void Conversion_KindIsImplicitOrExplicit()
    {
        var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");
        Assert.Contains(result.Operators, op => op.Kind == "implicit");
        Assert.Contains(result.Operators, op => op.Kind == "explicit");
    }

    [Fact]
    public void CheckedVariant_FlagSet()
    {
        var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");
        Assert.Contains(result.Operators, op => op.Kind == "+" && op.IsCheckedVariant);
    }

    [Fact]
    public void SynthesizedRecordEquality_Included()
    {
        var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");
        Assert.Contains(result.Operators, op => op.Kind == "==");
        Assert.Contains(result.Operators, op => op.Kind == "!=");
    }

    [Fact]
    public void Parameters_PopulatedWithTypeAndName()
    {
        var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");
        var addition = result.Operators.First(op => op.Kind == "+" && op.Parameters.Count == 2 && !op.IsCheckedVariant);

        Assert.Equal(2, addition.Parameters.Count);
        Assert.All(addition.Parameters, p =>
        {
            Assert.False(string.IsNullOrEmpty(p.Name));
            Assert.False(string.IsNullOrEmpty(p.Type));
        });
    }

    [Fact]
    public void XmlDocSummary_PopulatedForDocumentedOperator()
    {
        var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");
        var addition = result.Operators.FirstOrDefault(op =>
            op.Kind == "+" && op.Parameters.Count == 2 && !op.IsCheckedVariant);
        Assert.NotNull(addition);
        Assert.Equal("Add two amounts in the same currency.", addition!.XmlDocSummary);
    }

    [Fact]
    public void TypeWithNoOperators_ReturnsEmptyOperatorsButPopulatedContainingType()
    {
        var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "NoOperators");
        Assert.Equal("TestLib.NoOperators", result.ContainingType);
        Assert.Empty(result.Operators);
    }

    [Fact]
    public void UnknownType_ReturnsEmpty()
    {
        var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "TotallyMadeUpType");
        Assert.Equal(string.Empty, result.ContainingType);
        Assert.Empty(result.Operators);
    }

    [Fact]
    public void MetadataType_FindsBclOperators()
    {
        var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "System.Decimal");
        Assert.Contains("decimal", result.ContainingType, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Operators.Count >= 20,
            $"Expected ≥20 operators on System.Decimal, got {result.Operators.Count}");
        Assert.Contains(result.Operators, op => op.Kind == "+");
        Assert.Contains(result.Operators, op => op.Kind == "implicit");
    }
}
