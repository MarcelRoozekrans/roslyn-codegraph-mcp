using RoslynCodeLens;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindLargeClassesToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public FindLargeClassesToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void FindLargeClasses_LowThreshold_ReturnsResults()
    {
        var results = FindLargeClassesLogic.Execute(_loaded, _resolver, null, 1, 1);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void FindLargeClasses_HighThreshold_ReturnsEmpty()
    {
        var results = FindLargeClassesLogic.Execute(_loaded, _resolver, null, 1000, 10000);
        Assert.Empty(results);
    }

    [Fact]
    public void FindLargeClasses_ProjectFilter_FiltersResults()
    {
        var results = FindLargeClassesLogic.Execute(_loaded, _resolver, "TestLib2", 1, 1);
        Assert.All(results, r => Assert.Equal("TestLib2", r.Project));
    }
}
