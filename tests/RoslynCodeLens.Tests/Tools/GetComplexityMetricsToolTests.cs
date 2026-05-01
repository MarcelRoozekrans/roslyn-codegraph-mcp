using RoslynCodeLens;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetComplexityMetricsToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GetComplexityMetricsToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void GetComplexityMetrics_AllMethods_ReturnsResults()
    {
        var results = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 0);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void GetComplexityMetrics_HighThreshold_ReturnsEmpty()
    {
        var results = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 100);
        Assert.Empty(results);
    }

    [Fact]
    public void GetComplexityMetrics_ProjectFilter_FiltersResults()
    {
        _ = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 0);
        var filtered = GetComplexityMetricsLogic.Execute(_loaded, _resolver, "TestLib2", 0);
        Assert.All(filtered, r => Assert.Equal("TestLib2", r.Project));
    }
}
