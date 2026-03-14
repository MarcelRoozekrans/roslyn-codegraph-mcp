using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class GetCodeActionsLogicTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ExecuteAsync_AtMethodPosition_ReturnsCodeActions()
    {
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));
        var greeterPath = project.Documents.First(d => string.Equals(d.Name, "Greeter.cs", StringComparison.Ordinal)).FilePath!;

        var result = await GetCodeActionsLogic.ExecuteAsync(
            _loaded, greeterPath, line: 8, column: 5,
            endLine: null, endColumn: null, CancellationToken.None);

        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidFile_ReturnsEmpty()
    {
        var result = await GetCodeActionsLogic.ExecuteAsync(
            _loaded, "nonexistent.cs", line: 1, column: 1,
            endLine: null, endColumn: null, CancellationToken.None);

        Assert.Empty(result);
    }
}
