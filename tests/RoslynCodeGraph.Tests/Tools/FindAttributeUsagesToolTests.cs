using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class FindAttributeUsagesToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void FindAttributeUsages_Obsolete_FindsMarkedMember()
    {
        var results = FindAttributeUsagesLogic.Execute(_loaded, _resolver, "Obsolete");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.TargetName.Contains("OldGreet") && r.TargetKind == "method");
    }

    [Fact]
    public void FindAttributeUsages_Serializable_FindsMarkedType()
    {
        var results = FindAttributeUsagesLogic.Execute(_loaded, _resolver, "Serializable");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.TargetName.Contains("Greeter") && r.TargetKind == "class");
    }

    [Fact]
    public void FindAttributeUsages_WithSuffix_StillMatches()
    {
        var results = FindAttributeUsagesLogic.Execute(_loaded, _resolver, "ObsoleteAttribute");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.TargetName.Contains("OldGreet"));
    }

    [Fact]
    public void FindAttributeUsages_NoMatch_ReturnsEmpty()
    {
        var results = FindAttributeUsagesLogic.Execute(_loaded, _resolver, "NonExistentAttribute");

        Assert.Empty(results);
    }
}
