using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GoToDefinitionToolTests : IAsyncLifetime
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
    public void GoToDefinition_ForType_ReturnsLocation()
    {
        var results = GoToDefinitionLogic.Execute(_resolver, _metadata, "Greeter");

        Assert.Single(results);
        Assert.Contains("Greeter.cs", results[0].File, StringComparison.Ordinal);
        Assert.Equal("class", results[0].Type);
        Assert.Equal("source", results[0].Origin?.Kind);
    }

    [Fact]
    public void GoToDefinition_ForInterface_ReturnsLocation()
    {
        var results = GoToDefinitionLogic.Execute(_resolver, _metadata, "IGreeter");

        Assert.Single(results);
        Assert.Contains("IGreeter.cs", results[0].File, StringComparison.Ordinal);
        Assert.Equal("interface", results[0].Type);
    }

    [Fact]
    public void GoToDefinition_ForMethod_ReturnsLocation()
    {
        var results = GoToDefinitionLogic.Execute(_resolver, _metadata, "Greeter.Greet");

        Assert.NotEmpty(results);
        Assert.Equal("method", results[0].Type);
    }

    [Fact]
    public void GoToDefinition_UnknownSymbol_ReturnsEmpty()
    {
        var results = GoToDefinitionLogic.Execute(_resolver, _metadata, "NonExistent");

        Assert.Empty(results);
    }

    [Fact]
    public void GoToDefinition_MetadataType_ReturnsMetadataOrigin()
    {
        var result = GoToDefinitionLogic.Execute(
            _resolver, _metadata, "Microsoft.Extensions.DependencyInjection.IServiceCollection");

        var single = Assert.Single(result);
        Assert.NotNull(single.Origin);
        Assert.Equal("metadata", single.Origin!.Kind);
        Assert.Equal("", single.File);
        Assert.Equal(0, single.Line);
    }
}
