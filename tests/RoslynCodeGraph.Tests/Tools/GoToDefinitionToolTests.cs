using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class GoToDefinitionToolTests : IAsyncLifetime
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
    public void GoToDefinition_ForType_ReturnsLocation()
    {
        var results = GoToDefinitionLogic.Execute(_resolver, "Greeter");

        Assert.Single(results);
        Assert.Contains("Greeter.cs", results[0].File);
        Assert.Equal("class", results[0].Type);
    }

    [Fact]
    public void GoToDefinition_ForInterface_ReturnsLocation()
    {
        var results = GoToDefinitionLogic.Execute(_resolver, "IGreeter");

        Assert.Single(results);
        Assert.Contains("IGreeter.cs", results[0].File);
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
