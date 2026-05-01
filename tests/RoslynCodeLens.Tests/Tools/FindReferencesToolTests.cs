using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindReferencesToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public FindReferencesToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void FindReferences_ForInterface_ReturnsUsages()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, _metadata, "IGreeter");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.File.Contains("GreeterConsumer", StringComparison.Ordinal));
    }

    [Fact]
    public void FindReferences_ForMethod_ReturnsCallSites()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, _metadata, "IGreeter.Greet");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.File.Contains("GreeterConsumer", StringComparison.Ordinal));
    }

    [Fact]
    public void FindReferences_UnknownSymbol_ReturnsEmpty()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, _metadata, "NonExistent");

        Assert.Empty(results);
    }

    [Fact]
    public void FindReferences_MetadataInterface_FindsSourceUsages()
    {
        var results = FindReferencesLogic.Execute(
            _loaded, _resolver, _metadata,
            "Microsoft.Extensions.DependencyInjection.IServiceCollection");

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(!string.IsNullOrEmpty(r.File)));
    }
}
