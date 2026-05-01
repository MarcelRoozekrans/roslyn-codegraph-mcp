using RoslynCodeLens;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindReflectionUsageToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public FindReflectionUsageToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void FindReflection_DetectsActivatorCreateInstance()
    {
        var results = FindReflectionUsageLogic.Execute(_loaded, _resolver, null);

        Assert.Contains(results, r => string.Equals(r.Kind, "dynamic_instantiation", StringComparison.Ordinal)
            && r.Snippet.Contains("Activator.CreateInstance", StringComparison.Ordinal));
    }

    [Fact]
    public void FindReflection_DetectsTypeGetType()
    {
        var results = FindReflectionUsageLogic.Execute(_loaded, _resolver, null);

        Assert.Contains(results, r => r.Snippet.Contains("Type.GetType", StringComparison.Ordinal));
    }
}
