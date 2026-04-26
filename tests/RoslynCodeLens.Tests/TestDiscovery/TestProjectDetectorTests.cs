using RoslynCodeLens;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tests.TestDiscovery;

public class TestProjectDetectorTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void GetTestProjectIds_DetectsXUnitNUnitAndMSTest()
    {
        var ids = TestProjectDetector.GetTestProjectIds(_loaded.Solution);

        var names = ids
            .Select(id => _loaded.Solution.GetProject(id)!.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Contains("NUnitFixture", names);
        Assert.Contains("MSTestFixture", names);
    }

    [Fact]
    public void GetTestProjectIds_ExcludesNonTestProjects()
    {
        var ids = TestProjectDetector.GetTestProjectIds(_loaded.Solution);

        var names = ids
            .Select(id => _loaded.Solution.GetProject(id)!.Name)
            .ToList();

        Assert.DoesNotContain("TestLib", names);
        Assert.DoesNotContain("TestLib2", names);
    }
}
