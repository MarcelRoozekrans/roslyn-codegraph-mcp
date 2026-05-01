using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindImplementationsToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public FindImplementationsToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void FindImplementations_ForInterface_ReturnsImplementors()
    {
        var results = FindImplementationsLogic.Execute(_loaded, _resolver, _metadata, "IGreeter");

        Assert.Contains(results, r => r.FullName.Contains("Greeter", StringComparison.Ordinal));
        Assert.True(results.Count >= 1);
    }

    [Fact]
    public void FindImplementations_ForBaseClass_ReturnsDerived()
    {
        var results = FindImplementationsLogic.Execute(_loaded, _resolver, _metadata, "Greeter");

        Assert.Contains(results, r => r.FullName.Contains("FancyGreeter", StringComparison.Ordinal));
    }

    [Fact]
    public void FindImplementations_MetadataInterface_FindsSourceImplementors()
    {
        var results = FindImplementationsLogic.Execute(
            _loaded, _resolver, _metadata, "System.IDisposable");

        Assert.Contains(results, r => r.FullName.EndsWith("Greeter", StringComparison.Ordinal));
    }
}
