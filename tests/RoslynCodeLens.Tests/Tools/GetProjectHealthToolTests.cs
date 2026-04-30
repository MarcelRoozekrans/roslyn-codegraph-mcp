using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GetProjectHealthToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Result_HasOneEntryPerProductionProject()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: null, hotspotsPerDimension: 5);

        Assert.Contains(result.Projects, p => p.Project == "TestLib");
        Assert.Contains(result.Projects, p => p.Project == "TestLib2");
    }

    [Fact]
    public void TestProjectsAreSkipped()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: null, hotspotsPerDimension: 5);

        Assert.DoesNotContain(result.Projects, p => p.Project == "RoslynCodeLens.Tests");
    }

    [Fact]
    public void Counts_ComplexityMatchesUnderlyingTool()
    {
        var direct = GetComplexityMetricsLogic.Execute(_loaded, _resolver, "TestLib", 10);
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "TestLib", hotspotsPerDimension: 5);

        var entry = Assert.Single(result.Projects);
        Assert.Equal(direct.Count, entry.Counts.ComplexityHotspots);
    }

    [Fact]
    public void Counts_LargeClassesMatchesUnderlyingTool()
    {
        var direct = FindLargeClassesLogic.Execute(_loaded, _resolver, "TestLib", 20, 500);
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "TestLib", hotspotsPerDimension: 5);

        var entry = Assert.Single(result.Projects);
        Assert.Equal(direct.Count, entry.Counts.LargeClasses);
    }

    [Fact]
    public void Counts_NamingViolationsMatchesUnderlyingTool()
    {
        var direct = FindNamingViolationsLogic.Execute(_loaded, _resolver, "TestLib");
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "TestLib", hotspotsPerDimension: 5);

        var entry = Assert.Single(result.Projects);
        Assert.Equal(direct.Count, entry.Counts.NamingViolations);
    }

    [Fact]
    public void Counts_UnusedSymbolsMatchesUnderlyingTool()
    {
        var direct = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "TestLib", hotspotsPerDimension: 5);

        var entry = Assert.Single(result.Projects);
        Assert.Equal(direct.Count, entry.Counts.UnusedSymbols);
    }

    [Fact]
    public void Hotspots_TrimmedToRequestedSize()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: null, hotspotsPerDimension: 2);

        foreach (var p in result.Projects)
        {
            Assert.True(p.Hotspots.Complexity.Count <= 2);
            Assert.True(p.Hotspots.LargeClasses.Count <= 2);
            Assert.True(p.Hotspots.Naming.Count <= 2);
            Assert.True(p.Hotspots.Unused.Count <= 2);
            Assert.True(p.Hotspots.Reflection.Count <= 2);
            Assert.True(p.Hotspots.Async.Count <= 2);
            Assert.True(p.Hotspots.Disposable.Count <= 2);
        }
    }

    [Fact]
    public void ProjectFilter_IsCaseInsensitive()
    {
        // Underlying tools accept project names case-insensitively (OrdinalIgnoreCase Contains/Equals);
        // the composite must too, otherwise `get_project_health(project: "testlib")` would surprise.
        var lower = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "testlib", hotspotsPerDimension: 5);
        var upper = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "TESTLIB", hotspotsPerDimension: 5);
        var canonical = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "TestLib", hotspotsPerDimension: 5);

        Assert.Single(lower.Projects);
        Assert.Single(upper.Projects);
        Assert.Single(canonical.Projects);
        Assert.Equal("TestLib", lower.Projects[0].Project);
        Assert.Equal("TestLib", upper.Projects[0].Project);
    }

    [Fact]
    public void ProjectFilter_OnlyReturnsRequestedProject()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "TestLib", hotspotsPerDimension: 5);

        var entry = Assert.Single(result.Projects);
        Assert.Equal("TestLib", entry.Project);
    }

    [Fact]
    public void Hotspots_Complexity_SortedByCyclomaticDesc()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: null, hotspotsPerDimension: 10);

        foreach (var p in result.Projects)
        {
            for (int i = 1; i < p.Hotspots.Complexity.Count; i++)
            {
                Assert.True(
                    p.Hotspots.Complexity[i - 1].Complexity >= p.Hotspots.Complexity[i].Complexity,
                    $"Complexity hotspots not sorted desc in {p.Project} at index {i}");
            }
        }
    }

    [Fact]
    public void Hotspots_LargeClasses_SortedByLineCountDesc()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: null, hotspotsPerDimension: 10);

        foreach (var p in result.Projects)
        {
            for (int i = 1; i < p.Hotspots.LargeClasses.Count; i++)
            {
                Assert.True(
                    p.Hotspots.LargeClasses[i - 1].LineCount >= p.Hotspots.LargeClasses[i].LineCount,
                    $"LargeClasses hotspots not sorted desc in {p.Project} at index {i}");
            }
        }
    }

    [Fact]
    public void EmptyHotspotsRequest_StillPopulatesCounts()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "TestLib", hotspotsPerDimension: 0);
        var entry = Assert.Single(result.Projects);

        Assert.Empty(entry.Hotspots.Complexity);
        Assert.Empty(entry.Hotspots.LargeClasses);
        Assert.Empty(entry.Hotspots.Naming);
        Assert.Empty(entry.Hotspots.Unused);
        Assert.Empty(entry.Hotspots.Reflection);
        Assert.Empty(entry.Hotspots.Async);
        Assert.Empty(entry.Hotspots.Disposable);

        var totalCount = entry.Counts.ComplexityHotspots + entry.Counts.LargeClasses
                       + entry.Counts.NamingViolations + entry.Counts.UnusedSymbols
                       + entry.Counts.ReflectionUsages + entry.Counts.AsyncViolations
                       + entry.Counts.DisposableMisuse;
        Assert.True(totalCount > 0, "Expected TestLib to have at least one finding across all dimensions");
    }

    [Fact]
    public void UnknownProject_ReturnsEmptyList()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "DoesNotExist", hotspotsPerDimension: 5);

        Assert.Empty(result.Projects);
    }

    [Fact]
    public void ProjectsSorted_AscendingByName()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: null, hotspotsPerDimension: 5);

        for (int i = 1; i < result.Projects.Count; i++)
        {
            Assert.True(
                string.CompareOrdinal(result.Projects[i - 1].Project, result.Projects[i].Project) <= 0,
                $"Project sort violation at index {i}");
        }
    }
}
