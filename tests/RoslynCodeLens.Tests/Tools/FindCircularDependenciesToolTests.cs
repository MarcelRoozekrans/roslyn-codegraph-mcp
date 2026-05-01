using RoslynCodeLens;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindCircularDependenciesToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public FindCircularDependenciesToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void FindCircularDependencies_NoCycles_ReturnsEmpty()
    {
        var results = FindCircularDependenciesLogic.Execute(_loaded, _resolver, "project");
        Assert.Empty(results);
    }

    [Fact]
    public void FindCircularDependencies_InvalidLevel_ReturnsEmpty()
    {
        var results = FindCircularDependenciesLogic.Execute(_loaded, _resolver, "invalid");
        Assert.Empty(results);
    }
}
