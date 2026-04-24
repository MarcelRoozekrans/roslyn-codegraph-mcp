using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class AnalyzeChangeImpactToolTests : IAsyncLifetime
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
    public void Execute_ForInterfaceMethod_ReturnsImpact()
    {
        var result = AnalyzeChangeImpactLogic.Execute(_loaded, _resolver, _metadata, "IGreeter.Greet");

        Assert.NotNull(result);
        Assert.True(result.DirectReferenceCount > 0 || result.CallerCount > 0);
        Assert.NotEmpty(result.AffectedFiles);
        Assert.NotEmpty(result.AffectedProjects);
    }

    [Fact]
    public void Execute_ForUnknownSymbol_ReturnsNull()
    {
        var result = AnalyzeChangeImpactLogic.Execute(_loaded, _resolver, _metadata, "NonExistentClass.NoMethod");

        Assert.Null(result);
    }
}
