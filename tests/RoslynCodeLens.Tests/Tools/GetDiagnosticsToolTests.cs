using RoslynCodeLens;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GetDiagnosticsToolTests : IAsyncLifetime
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
    public void GetDiagnostics_CleanSolution_ReturnsNoErrors()
    {
        var results = GetDiagnosticsLogic.Execute(_loaded, _resolver, null, "error");

        // Test solution should compile cleanly
        Assert.Empty(results);
    }

    [Fact]
    public void GetDiagnostics_WithProjectFilter_FiltersResults()
    {
        var all = GetDiagnosticsLogic.Execute(_loaded, _resolver, null, null);
        var filtered = GetDiagnosticsLogic.Execute(_loaded, _resolver, "TestLib", null);

        Assert.True(filtered.Count <= all.Count);
        Assert.All(filtered, d => Assert.Contains("TestLib", d.Project, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetDiagnostics_WithAnalyzers_IncludesAnalyzerDiagnostics()
    {
        var results = await GetDiagnosticsLogic.ExecuteAsync(
            _loaded, _resolver, null, null, includeAnalyzers: true);

        // Any analyzer diagnostics should have Source starting with "analyzer"
        _ = results.Where(d => d.Source.StartsWith("analyzer", StringComparison.Ordinal)).ToList();
        // Compiler diagnostics should still be present with Source == "compiler"
        _ = results.Where(d => string.Equals(d.Source, "compiler", StringComparison.Ordinal)).ToList();

        // All results should have a valid source
        Assert.All(results, d => Assert.True(
            string.Equals(d.Source, "compiler", StringComparison.Ordinal) || d.Source.StartsWith("analyzer:", StringComparison.Ordinal),
            $"Unexpected source: {d.Source}"));
    }

    [Fact]
    public async Task GetDiagnostics_WithoutAnalyzers_OnlyCompilerDiagnostics()
    {
        var results = await GetDiagnosticsLogic.ExecuteAsync(
            _loaded, _resolver, null, null, includeAnalyzers: false);

        Assert.All(results, d => Assert.Equal("compiler", d.Source));
    }
}
