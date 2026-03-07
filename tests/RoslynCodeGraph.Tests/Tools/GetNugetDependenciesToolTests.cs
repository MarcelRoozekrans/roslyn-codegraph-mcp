using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class GetNugetDependenciesToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void GetNugetDependencies_AllProjects_ReturnsPackages()
    {
        var result = GetNugetDependenciesLogic.Execute(_loaded, null);

        Assert.NotNull(result);
        Assert.NotEmpty(result!.Packages);
        Assert.Contains(result.Packages, p => p.PackageName == "Microsoft.Extensions.DependencyInjection.Abstractions");
    }

    [Fact]
    public void GetNugetDependencies_FilterByProject_ReturnsOnlyThatProject()
    {
        var result = GetNugetDependenciesLogic.Execute(_loaded, "TestLib2");

        Assert.NotNull(result);
        Assert.All(result!.Packages, p => Assert.Equal("TestLib2", p.Project));
    }

    [Fact]
    public void GetNugetDependencies_IncludesVersion()
    {
        var result = GetNugetDependenciesLogic.Execute(_loaded, null);

        Assert.NotNull(result);
        var diPkg = result!.Packages.First(p => p.PackageName == "Microsoft.Extensions.DependencyInjection.Abstractions");
        Assert.False(string.IsNullOrEmpty(diPkg.Version));
    }

    [Fact]
    public void GetNugetDependencies_UnknownProject_ReturnsEmpty()
    {
        var result = GetNugetDependenciesLogic.Execute(_loaded, "NonExistentProject");

        Assert.NotNull(result);
        Assert.Empty(result!.Packages);
    }
}
