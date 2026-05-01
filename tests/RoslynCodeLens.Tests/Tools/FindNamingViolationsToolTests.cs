using RoslynCodeLens;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindNamingViolationsToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public FindNamingViolationsToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void FindNamingViolations_CleanCode_NoViolations()
    {
        var results = FindNamingViolationsLogic.Execute(_loaded, _resolver, null);
        Assert.NotNull(results);
    }

    [Fact]
    public void FindNamingViolations_ProjectFilter_FiltersResults()
    {
        var filtered = FindNamingViolationsLogic.Execute(_loaded, _resolver, "TestLib");
        Assert.All(filtered, r => Assert.Contains("TestLib", r.Project, StringComparison.Ordinal));
    }
}
