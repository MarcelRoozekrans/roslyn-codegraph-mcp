using RoslynCodeLens;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetNugetDependenciesToolTests
{
    private readonly LoadedSolution _loaded;

    public GetNugetDependenciesToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
    }

    [Fact]
    public void GetNugetDependencies_AllProjects_ReturnsPackages()
    {
        var result = GetNugetDependenciesLogic.Execute(_loaded, null);

        Assert.NotNull(result);
        Assert.NotEmpty(result!.Packages);
        Assert.Contains(result.Packages, p => string.Equals(p.PackageName, "Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal));
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
        var diPkg = result!.Packages.First(p => string.Equals(p.PackageName, "Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal));
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
