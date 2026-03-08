using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class FindReferencesToolTests : IAsyncLifetime
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
    public void FindReferences_ForInterface_ReturnsUsages()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, "IGreeter");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.File.Contains("GreeterConsumer", StringComparison.Ordinal));
    }

    [Fact]
    public void FindReferences_ForMethod_ReturnsCallSites()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, "IGreeter.Greet");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.File.Contains("GreeterConsumer", StringComparison.Ordinal));
    }

    [Fact]
    public void FindReferences_UnknownSymbol_ReturnsEmpty()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, "NonExistent");

        Assert.Empty(results);
    }
}
