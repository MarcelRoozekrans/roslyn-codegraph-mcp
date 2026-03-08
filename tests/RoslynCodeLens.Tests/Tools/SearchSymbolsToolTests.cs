using RoslynCodeLens;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class SearchSymbolsToolTests : IAsyncLifetime
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
    public void SearchSymbols_ByTypeName_FindsTypes()
    {
        var results = SearchSymbolsLogic.Execute(_resolver, "Greeter");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.FullName.Contains("Greeter", StringComparison.Ordinal));
    }

    [Fact]
    public void SearchSymbols_ByMethodName_FindsMethods()
    {
        var results = SearchSymbolsLogic.Execute(_resolver, "Greet");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => string.Equals(r.Type, "method", StringComparison.Ordinal));
    }

    [Fact]
    public void SearchSymbols_CaseInsensitive_Works()
    {
        var results = SearchSymbolsLogic.Execute(_resolver, "greeter");

        Assert.NotEmpty(results);
    }

    [Fact]
    public void SearchSymbols_NoMatch_ReturnsEmpty()
    {
        var results = SearchSymbolsLogic.Execute(_resolver, "XyzNonExistent123");

        Assert.Empty(results);
    }
}
