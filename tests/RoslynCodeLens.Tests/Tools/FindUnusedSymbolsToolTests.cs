using RoslynCodeLens;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindUnusedSymbolsToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void FindUnusedSymbols_ReturnsResults()
    {
        var results = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, null, false);
        Assert.NotNull(results);
    }

    [Fact]
    public void FindUnusedSymbols_ProjectFilter_FiltersResults()
    {
        var results = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);
        Assert.All(results, r => Assert.Contains("TestLib", r.Project, StringComparison.Ordinal));
    }
}
