using RoslynCodeLens;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GetTypeOverviewToolTests : IAsyncLifetime
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
    public void Execute_ForGreeter_ReturnsFullOverview()
    {
        var result = GetTypeOverviewLogic.Execute(_loaded, _resolver, "Greeter");

        Assert.NotNull(result);
        Assert.NotNull(result.Context);
        Assert.NotNull(result.Hierarchy);
        Assert.NotEmpty(result.Context.PublicMembers);
        Assert.NotEmpty(result.Hierarchy.Interfaces);
    }

    [Fact]
    public void Execute_ForUnknownType_ReturnsNull()
    {
        var result = GetTypeOverviewLogic.Execute(_loaded, _resolver, "NonExistentType99");

        Assert.Null(result);
    }
}
