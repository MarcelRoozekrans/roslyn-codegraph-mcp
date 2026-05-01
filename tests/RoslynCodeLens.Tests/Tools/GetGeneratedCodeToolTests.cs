using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetGeneratedCodeToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GetGeneratedCodeToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void Execute_ReturnsEmpty_WhenFileNotFound()
    {
        var results = GetGeneratedCodeLogic.Execute(_loaded, _resolver, null, "nonexistent.g.cs");
        Assert.Empty(results);
    }

    [Fact]
    public void Execute_ReturnsEmpty_WhenGeneratorNotFound()
    {
        var results = GetGeneratedCodeLogic.Execute(_loaded, _resolver, "NonExistentGenerator", null);
        Assert.Empty(results);
    }
}
