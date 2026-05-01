using RoslynCodeLens;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetProjectDependenciesToolTests
{
    private readonly LoadedSolution _loaded;

    public GetProjectDependenciesToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
    }

    [Fact]
    public void GetDependencies_ForTestLib2_ReturnsTestLib()
    {
        var result = GetProjectDependenciesLogic.Execute(_loaded, "TestLib2");

        Assert.NotNull(result);
        Assert.Contains(result.Direct, d => string.Equals(d.Name, "TestLib", StringComparison.Ordinal));
    }
}
