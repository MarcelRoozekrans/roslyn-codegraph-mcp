using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class FindLargeClassesToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void FindLargeClasses_LowThreshold_ReturnsResults()
    {
        var results = FindLargeClassesLogic.Execute(_loaded, _resolver, null, 1, 1);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void FindLargeClasses_HighThreshold_ReturnsEmpty()
    {
        var results = FindLargeClassesLogic.Execute(_loaded, _resolver, null, 1000, 10000);
        Assert.Empty(results);
    }

    [Fact]
    public void FindLargeClasses_ProjectFilter_FiltersResults()
    {
        var results = FindLargeClassesLogic.Execute(_loaded, _resolver, "TestLib2", 1, 1);
        Assert.All(results, r => Assert.Equal("TestLib2", r.Project));
    }
}
