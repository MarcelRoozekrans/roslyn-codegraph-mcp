using RoslynCodeLens;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class AnalyzeDataFlowToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly string _greeterConsumerPath;

    public AnalyzeDataFlowToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _greeterConsumerPath = _loaded.Solution.Projects
            .First(p => string.Equals(p.Name, "TestLib2", StringComparison.Ordinal))
            .Documents.First(d => string.Equals(d.Name, "GreeterConsumer.cs", StringComparison.Ordinal))
            .FilePath!;
    }

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
