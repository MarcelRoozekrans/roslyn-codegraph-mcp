using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GetTypeOverviewToolTests : IAsyncLifetime
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
    public void Execute_ForGreeter_ReturnsFullOverview()
    {
        var result = GetTypeOverviewLogic.Execute(_loaded, _resolver, _metadata, "Greeter");

        Assert.NotNull(result);
        Assert.NotNull(result.Context);
        Assert.NotNull(result.Hierarchy);
        Assert.NotEmpty(result.Context.PublicMembers);
        Assert.NotEmpty(result.Hierarchy.Interfaces);
        Assert.Equal("source", result.Origin?.Kind);
    }

    [Fact]
    public void Execute_ForUnknownType_ReturnsNull()
    {
        var result = GetTypeOverviewLogic.Execute(_loaded, _resolver, _metadata, "NonExistentType99");

        Assert.Null(result);
    }

    [Fact]
    public void TypeOverview_MetadataInterface_ReturnsShapeWithOrigin()
    {
        var result = GetTypeOverviewLogic.Execute(_loaded, _resolver, _metadata,
            "Microsoft.Extensions.DependencyInjection.IServiceCollection");

        Assert.NotNull(result);
        Assert.Equal("metadata", result!.Origin?.Kind);
        Assert.NotNull(result.Context);
        Assert.NotEmpty(result.Context!.PublicMembers);
        Assert.Empty(result.Diagnostics);
    }
}
