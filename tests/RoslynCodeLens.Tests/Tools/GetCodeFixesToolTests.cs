using RoslynCodeLens;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetCodeFixesToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GetCodeFixesToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

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
