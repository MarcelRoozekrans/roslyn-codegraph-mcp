using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindCallersToolTests : IAsyncLifetime
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
    public void FindCallers_ForMethod_ReturnsCallSites()
    {
        var results = FindCallersLogic.Execute(_loaded, _resolver, _metadata, "IGreeter.Greet");

        Assert.Contains(results, r => r.Caller.Contains("GreeterConsumer", StringComparison.Ordinal));
    }

    [Fact]
    public void FindCallers_MetadataExtensionMethod_FindsSourceInvocations()
    {
        var results = FindCallersLogic.Execute(
            _loaded, _resolver, _metadata,
            "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped");

        Assert.NotEmpty(results);
    }
}
