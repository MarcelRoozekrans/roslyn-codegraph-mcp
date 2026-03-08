using RoslynCodeLens;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GetSymbolContextToolTests : IAsyncLifetime
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
    public void GetContext_ForGreeterConsumer_ShowsInjectedDeps()
    {
        var result = GetSymbolContextLogic.Execute(_loaded, _resolver, "GreeterConsumer");

        Assert.NotNull(result);
        Assert.Equal("TestLib2", result.Namespace);
        Assert.Contains(result.InjectedDependencies, d => d.Contains("IGreeter", StringComparison.Ordinal));
        Assert.Contains(result.PublicMembers, m => m.Contains("SayHello", StringComparison.Ordinal));
    }
}
