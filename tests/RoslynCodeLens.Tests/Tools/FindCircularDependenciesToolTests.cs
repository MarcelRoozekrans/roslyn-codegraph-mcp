using RoslynCodeLens;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindCircularDependenciesToolTests : IAsyncLifetime
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
    public void FindCircularDependencies_NoCycles_ReturnsEmpty()
    {
        var results = FindCircularDependenciesLogic.Execute(_loaded, _resolver, "project");
        Assert.Empty(results);
    }

    [Fact]
    public void FindCircularDependencies_InvalidLevel_ReturnsEmpty()
    {
        var results = FindCircularDependenciesLogic.Execute(_loaded, _resolver, "invalid");
        Assert.Empty(results);
    }
}
