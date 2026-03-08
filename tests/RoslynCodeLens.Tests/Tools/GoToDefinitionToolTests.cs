using RoslynCodeLens;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GoToDefinitionToolTests : IAsyncLifetime
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
    public void GoToDefinition_ForType_ReturnsLocation()
    {
        var results = GoToDefinitionLogic.Execute(_resolver, "Greeter");

        Assert.Single(results);
        Assert.Contains("Greeter.cs", results[0].File, StringComparison.Ordinal);
        Assert.Equal("class", results[0].Type);
    }

    [Fact]
    public void GoToDefinition_ForInterface_ReturnsLocation()
    {
        var results = GoToDefinitionLogic.Execute(_resolver, "IGreeter");

        Assert.Single(results);
        Assert.Contains("IGreeter.cs", results[0].File, StringComparison.Ordinal);
        Assert.Equal("interface", results[0].Type);
    }

    [Fact]
    public void GoToDefinition_ForMethod_ReturnsLocation()
    {
        var results = GoToDefinitionLogic.Execute(_resolver, "Greeter.Greet");

        Assert.NotEmpty(results);
        Assert.Equal("method", results[0].Type);
    }

    [Fact]
    public void GoToDefinition_UnknownSymbol_ReturnsEmpty()
    {
        var results = GoToDefinitionLogic.Execute(_resolver, "NonExistent");

        Assert.Empty(results);
    }
}
