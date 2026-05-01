using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GetOverloadsToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;
    private MetadataSymbolResolver _metadata = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _resolver = new SymbolResolver(_loaded);
        _metadata = new MetadataSymbolResolver(_loaded, _resolver);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Result_ReturnsAllOverloads()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Add");

        Assert.Contains("OverloadSamples", result.ContainingType, StringComparison.Ordinal);
        Assert.Equal(3, result.Overloads.Count);
    }

    [Fact]
    public void Result_SortedByParameterCountThenSignature()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Add");

        for (int i = 1; i < result.Overloads.Count; i++)
        {
            var prev = result.Overloads[i - 1];
            var curr = result.Overloads[i];
            if (prev.Parameters.Count == curr.Parameters.Count)
                Assert.True(string.CompareOrdinal(prev.Signature, curr.Signature) <= 0,
                    $"Sort violation: '{prev.Signature}' before '{curr.Signature}'");
            else
                Assert.True(prev.Parameters.Count < curr.Parameters.Count,
                    $"Sort violation by param count");
        }
    }

    [Fact]
    public void Parameters_IncludeNamesAndTypes()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Add");

        var twoParam = result.Overloads.Single(o => o.Parameters.Count == 2);
        Assert.Equal("a", twoParam.Parameters[0].Name);
        Assert.Equal("b", twoParam.Parameters[1].Name);
        Assert.Contains("int", twoParam.Parameters[0].Type, StringComparison.Ordinal);
    }

    [Fact]
    public void Parameters_HasParamsFlag()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Add");

        var paramsOverload = result.Overloads.Single(o =>
            o.Parameters.Count == 1 && o.Parameters[0].IsParams);
        Assert.Equal("values", paramsOverload.Parameters[0].Name);
    }

    [Fact]
    public void Parameters_HasOptionalDefault()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Echo");

        var echo = Assert.Single(result.Overloads);
        var times = echo.Parameters[1];
        Assert.Equal("times", times.Name);
        Assert.True(times.IsOptional);
        Assert.Equal("1", times.DefaultValue);
    }

    [Fact]
    public void GenericMethod_TypeParametersPopulated()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Add");

        var generic = result.Overloads.Single(o => o.TypeParameters.Count > 0);
        Assert.Contains("TKey", generic.TypeParameters);
    }

    [Fact]
    public void XmlDocSummary_PopulatedForDocumentedMethod()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Add");

        Assert.All(result.Overloads, o =>
        {
            Assert.NotNull(o.XmlDocSummary);
            Assert.NotEmpty(o.XmlDocSummary!);
        });
    }

    [Fact]
    public void ExtensionMethod_FlagSet()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadExtensions.Doubled");

        Assert.NotEmpty(result.Overloads);
        Assert.All(result.Overloads, o => Assert.True(o.IsExtensionMethod));
    }

    [Fact]
    public void StaticMethod_IsStaticFlagSet()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.FromString");

        Assert.Equal(2, result.Overloads.Count);
        Assert.All(result.Overloads, o => Assert.True(o.IsStatic));
    }

    [Fact]
    public void MetadataMethod_FindsAllBclOverloads()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "System.Console.WriteLine");

        // BCL Console.WriteLine on net10 has 19 public overloads; lock to >= 18 so a metadata-
        // fallback regression that returns only the first match would be caught.
        Assert.True(result.Overloads.Count >= 18,
            $"Expected >=18 Console.WriteLine overloads, got {result.Overloads.Count}");
    }

    [Fact]
    public void Parameters_RenderRefKindModifiers()
    {
        // Verify each RefKind variant survives BuildParameter — including `ref readonly`
        // (RefKind.RefReadOnlyParameter) which previously fell through to empty modifier.
        var tryParse = Assert.Single(GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.TryParse").Overloads);
        Assert.Equal("out", tryParse.Parameters.Single(p => p.Name == "value").Modifier);

        var increment = Assert.Single(GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Increment").Overloads);
        Assert.Equal("ref", increment.Parameters.Single(p => p.Name == "value").Modifier);

        var inspectIn = Assert.Single(GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.InspectIn").Overloads);
        Assert.Equal("in", inspectIn.Parameters.Single(p => p.Name == "value").Modifier);

        var inspectRefReadonly = Assert.Single(GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.InspectRefReadonly").Overloads);
        Assert.Equal("ref readonly", inspectRefReadonly.Parameters.Single(p => p.Name == "value").Modifier);
    }

    [Fact]
    public void Constructors_ReturnsCtorOverloads()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "Greeter.Greeter");

        // Greeter has at least an implicit/declared constructor.
        Assert.NotEmpty(result.Overloads);
    }

    [Fact]
    public void UnknownSymbol_ReturnsEmpty()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "Does.Not.Exist");

        Assert.Empty(result.ContainingType);
        Assert.Empty(result.Overloads);
    }

    [Fact]
    public void OperatorsExcluded()
    {
        // OverloadSamples doesn't define operators; querying Add must not include any operator-kind methods.
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Add");

        Assert.All(result.Overloads, o =>
            Assert.DoesNotContain("op_", o.Signature, StringComparison.Ordinal));
    }
}
