using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class GetCodeFixesToolTests : IAsyncLifetime
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
    public async Task GetCodeFixes_NoMatchingDiagnostic_ReturnsEmpty()
    {
        var results = await GetCodeFixesLogic.ExecuteAsync(
            _loaded, _resolver, "FAKE999", "NonExistent.cs", 1, CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetCodeFixes_ReturnsSuggestions()
    {
        var diagnostics = GetDiagnosticsLogic.Execute(_loaded, _resolver, null, null);
        var diag = diagnostics.FirstOrDefault(d => !string.IsNullOrEmpty(d.File));

        if (diag == null) return;

        var results = await GetCodeFixesLogic.ExecuteAsync(
            _loaded, _resolver, diag.Id, diag.File, diag.Line, CancellationToken.None);
        Assert.NotNull(results);
    }
}
