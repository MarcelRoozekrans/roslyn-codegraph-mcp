using RoslynCodeLens;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class AnalyzeDataFlowToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private string _greeterConsumerPath = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _greeterConsumerPath = _loaded.Solution.Projects
            .First(p => string.Equals(p.Name, "TestLib2", StringComparison.Ordinal))
            .Documents.First(d => string.Equals(d.Name, "GreeterConsumer.cs", StringComparison.Ordinal))
            .FilePath!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ExecuteAsync_OnBlockBodyStatement_ReturnsFlowInfo()
    {
        // GreeterConsumer.cs constructor body: line 11 is `_greeter = greeter;`
        var result = await AnalyzeDataFlowLogic.ExecuteAsync(
            _loaded, _greeterConsumerPath, startLine: 11, endLine: 11, CancellationToken.None);

        Assert.NotNull(result);
        // `greeter` parameter flows into this assignment
        Assert.Contains("greeter", result.DataFlowsIn);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidFile_ReturnsNull()
    {
        var result = await AnalyzeDataFlowLogic.ExecuteAsync(
            _loaded, "nonexistent.cs", startLine: 1, endLine: 1, CancellationToken.None);

        Assert.Null(result);
    }
}
