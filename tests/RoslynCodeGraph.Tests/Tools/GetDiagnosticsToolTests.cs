using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class GetDiagnosticsToolTests : IAsyncLifetime
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
    public void GetDiagnostics_CleanSolution_ReturnsNoErrors()
    {
        var results = GetDiagnosticsLogic.Execute(_loaded, _resolver, null, "error");

        // Test solution should compile cleanly
        Assert.Empty(results);
    }

    [Fact]
    public void GetDiagnostics_WithProjectFilter_FiltersResults()
    {
        var all = GetDiagnosticsLogic.Execute(_loaded, _resolver, null, null);
        var filtered = GetDiagnosticsLogic.Execute(_loaded, _resolver, "TestLib", null);

        Assert.True(filtered.Count <= all.Count);
        Assert.All(filtered, d => Assert.Contains("TestLib", d.Project));
    }
}
