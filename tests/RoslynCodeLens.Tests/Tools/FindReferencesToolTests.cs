using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindReferencesToolTests : IAsyncLifetime
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
    public void FindReferences_ForInterface_ReturnsUsages()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, _metadata, "IGreeter");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.File.Contains("GreeterConsumer", StringComparison.Ordinal));
    }

    [Fact]
    public void FindReferences_ForMethod_ReturnsCallSites()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, _metadata, "IGreeter.Greet");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.File.Contains("GreeterConsumer", StringComparison.Ordinal));
    }

    [Fact]
    public void FindReferences_UnknownSymbol_ReturnsEmpty()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, _metadata, "NonExistent");

        Assert.Empty(results);
    }

    [Fact]
    public void FindReferences_MetadataInterface_FindsSourceUsages()
    {
        var results = FindReferencesLogic.Execute(
            _loaded, _resolver, _metadata,
            "Microsoft.Extensions.DependencyInjection.IServiceCollection");

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(!string.IsNullOrEmpty(r.File)));
    }
}
