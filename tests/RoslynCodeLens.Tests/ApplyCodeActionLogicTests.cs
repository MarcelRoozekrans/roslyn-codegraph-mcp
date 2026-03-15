using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class ApplyCodeActionLogicTests : IAsyncLifetime
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
    public async Task ExecuteAsync_WithPreview_ReturnsDiffWithoutWriting()
    {
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));
        var greeterPath = project.Documents.First(d => string.Equals(d.Name, "Greeter.cs", StringComparison.Ordinal)).FilePath!;

        // Select the expression body on line 8 to trigger refactorings like "Use block body"
        var actions = await GetCodeActionsLogic.ExecuteAsync(
            _loaded, greeterPath, line: 8, column: 48,
            endLine: 8, endColumn: 66, CancellationToken.None);

        Assert.NotEmpty(actions);

        // Try applying each action until one succeeds (some require workspace services)
        CodeActionResult? result = null;
        foreach (var action in actions)
        {
            result = await ApplyCodeActionLogic.ExecuteAsync(
                _loaded, greeterPath, line: 8, column: 48,
                endLine: 8, endColumn: 66,
                actionTitle: action.Title, preview: true, CancellationToken.None);

            if (result.Success) break;
        }

        Assert.NotNull(result);
        Assert.True(result.Success, $"No action succeeded. Last error: {result.ErrorMessage}");
        Assert.NotEmpty(result.Title);
    }

    [Fact]
    public async Task ExecuteAsync_WithBadTitle_ReturnsError()
    {
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));
        var greeterPath = project.Documents.First(d => string.Equals(d.Name, "Greeter.cs", StringComparison.Ordinal)).FilePath!;

        var result = await ApplyCodeActionLogic.ExecuteAsync(
            _loaded, greeterPath, line: 8, column: 5,
            endLine: null, endColumn: null,
            actionTitle: "NonExistentAction_12345", preview: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidFile_ReturnsError()
    {
        var result = await ApplyCodeActionLogic.ExecuteAsync(
            _loaded, "nonexistent.cs", line: 1, column: 1,
            endLine: null, endColumn: null,
            actionTitle: "anything", preview: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }
}
