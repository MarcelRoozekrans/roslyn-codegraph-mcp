using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class FindReflectionUsageToolTests : IAsyncLifetime
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
    public void FindReflection_DetectsActivatorCreateInstance()
    {
        var results = FindReflectionUsageLogic.Execute(_loaded, _resolver, null);

        Assert.Contains(results, r => string.Equals(r.Kind, "dynamic_instantiation", StringComparison.Ordinal)
            && r.Snippet.Contains("Activator.CreateInstance", StringComparison.Ordinal));
    }

    [Fact]
    public void FindReflection_DetectsTypeGetType()
    {
        var results = FindReflectionUsageLogic.Execute(_loaded, _resolver, null);

        Assert.Contains(results, r => r.Snippet.Contains("Type.GetType", StringComparison.Ordinal));
    }
}
