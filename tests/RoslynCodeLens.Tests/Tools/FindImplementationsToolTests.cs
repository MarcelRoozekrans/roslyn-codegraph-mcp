using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindImplementationsToolTests : IAsyncLifetime
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
