using RoslynCodeLens;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GetFileOverviewToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;
    private string _greeterPath = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _resolver = new SymbolResolver(_loaded);
        _greeterPath = _loaded.Solution.Projects
            .First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal))
            .Documents.First(d => string.Equals(d.Name, "Greeter.cs", StringComparison.Ordinal))
            .FilePath!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ExecuteAsync_ForGreeterFile_ReturnsOverview()
    {
        var result = await GetFileOverviewLogic.ExecuteAsync(
            _loaded, _resolver, _greeterPath, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result.TypesDefined);
        Assert.Contains("Greeter", result.TypesDefined);
        Assert.NotNull(result.Project);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidFile_ReturnsNull()
    {
        var result = await GetFileOverviewLogic.ExecuteAsync(
            _loaded, _resolver, "nonexistent.cs", CancellationToken.None);

        Assert.Null(result);
    }
}
