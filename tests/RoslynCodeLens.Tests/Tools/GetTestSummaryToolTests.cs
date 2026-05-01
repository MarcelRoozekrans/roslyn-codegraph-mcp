using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetTestSummaryToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GetTestSummaryToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void Result_FindsXUnitTests()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);

        var xUnitProject = Assert.Single(result.Projects, p => p.Project == "XUnitFixture");
        Assert.True(xUnitProject.TotalTests > 0);
        Assert.Contains("XUnit", xUnitProject.ByFramework.Keys);
    }

    [Fact]
    public void Result_FindsNUnitTests()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);

        var nUnitProject = Assert.Single(result.Projects, p => p.Project == "NUnitFixture");
        Assert.True(nUnitProject.TotalTests > 0);
        Assert.Contains("NUnit", nUnitProject.ByFramework.Keys);
    }

    [Fact]
    public void Result_FindsMSTestTests()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);

        var msTestProject = Assert.Single(result.Projects, p => p.Project == "MSTestFixture");
        Assert.True(msTestProject.TotalTests > 0);
        Assert.Contains("MSTest", msTestProject.ByFramework.Keys);
    }

    [Fact]
    public void InlineDataRowCount_PopulatedForXUnitTheory()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: "XUnitFixture");

        var project = Assert.Single(result.Projects);
        // SampleTests has a [Theory] with [InlineData] rows — count must be > 0.
        var theory = project.Tests.FirstOrDefault(t =>
            string.Equals(t.AttributeShortName, "Theory", StringComparison.Ordinal));
        Assert.NotNull(theory);
        Assert.True(theory!.InlineDataRowCount > 0,
            $"Expected InlineDataRowCount > 0 for theory {theory.MethodName}, got {theory.InlineDataRowCount}");
    }

    [Fact]
    public void InlineDataRowCount_ZeroForFact()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: "XUnitFixture");

        var project = Assert.Single(result.Projects);
        var fact = project.Tests.FirstOrDefault(t =>
            string.Equals(t.AttributeShortName, "Fact", StringComparison.Ordinal));
        Assert.NotNull(fact);
        Assert.Equal(0, fact!.InlineDataRowCount);
    }

    [Fact]
    public void ReferencedSymbols_IncludeProductionCalls()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: "XUnitFixture");

        var project = Assert.Single(result.Projects);
        // SampleTests calls Greeter.Greet — every test should list a TestLib production symbol.
        Assert.Contains(project.Tests, t =>
            t.ReferencedSymbols.Any(s => s.Contains("TestLib", StringComparison.Ordinal)));
    }

    [Fact]
    public void ReferencedSymbols_ExcludesFrameworkAndBcl()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: "XUnitFixture");

        var project = Assert.Single(result.Projects);
        foreach (var test in project.Tests)
        {
            foreach (var symbol in test.ReferencedSymbols)
            {
                Assert.False(symbol.StartsWith("Xunit", StringComparison.Ordinal),
                    $"Test {test.MethodName} referenced framework symbol {symbol}");
                Assert.False(symbol.StartsWith("System.", StringComparison.Ordinal) || symbol == "System",
                    $"Test {test.MethodName} referenced BCL symbol {symbol}");
                Assert.False(symbol.StartsWith("Microsoft.", StringComparison.Ordinal) || symbol == "Microsoft",
                    $"Test {test.MethodName} referenced Microsoft.* symbol {symbol}");
                Assert.False(symbol.StartsWith("NUnit.Framework", StringComparison.Ordinal),
                    $"Test {test.MethodName} referenced NUnit framework symbol {symbol}");
            }
        }
    }

    [Fact]
    public void ProjectFilter_OnlyReturnsRequestedProject()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: "XUnitFixture");

        var project = Assert.Single(result.Projects);
        Assert.Equal("XUnitFixture", project.Project);
    }

    [Fact]
    public void Result_OmitsProductionProjects()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);

        Assert.DoesNotContain(result.Projects, p => p.Project == "TestLib");
        Assert.DoesNotContain(result.Projects, p => p.Project == "TestLib2");
    }

    [Fact]
    public void ByFramework_CountsAreCorrect()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);

        foreach (var project in result.Projects)
        {
            foreach (var (frameworkName, count) in project.ByFramework)
            {
                var actual = project.Tests.Count(t =>
                    string.Equals(t.Framework, frameworkName, StringComparison.Ordinal));
                Assert.Equal(actual, count);
            }
        }
    }

    [Fact]
    public void ByAttribute_CountsAreCorrect()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);

        foreach (var project in result.Projects)
        {
            foreach (var (attrName, count) in project.ByAttribute)
            {
                var actual = project.Tests.Count(t =>
                    string.Equals(t.AttributeShortName, attrName, StringComparison.Ordinal));
                Assert.Equal(actual, count);
            }
        }
    }

    [Fact]
    public void Tests_SortedByFileLine()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);

        foreach (var project in result.Projects)
        {
            for (int i = 1; i < project.Tests.Count; i++)
            {
                var prev = project.Tests[i - 1];
                var curr = project.Tests[i];
                var fileCmp = string.CompareOrdinal(prev.FilePath, curr.FilePath);
                Assert.True(fileCmp < 0 || (fileCmp == 0 && prev.Line <= curr.Line),
                    $"Sort violation in {project.Project} at index {i}: '{prev.FilePath}:{prev.Line}' before '{curr.FilePath}:{curr.Line}'");
            }
        }
    }

    [Fact]
    public void UnknownProject_ReturnsEmptyList()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: "DoesNotExist");

        Assert.Empty(result.Projects);
    }

    [Fact]
    public void Tests_PopulatedWithMethodNameAndLocation()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);

        Assert.NotEmpty(result.Projects);
        foreach (var project in result.Projects)
        {
            Assert.All(project.Tests, t =>
            {
                Assert.NotEmpty(t.MethodName);
                Assert.NotEmpty(t.Framework);
                Assert.NotEmpty(t.AttributeShortName);
                Assert.NotEmpty(t.FilePath);
                Assert.True(t.Line > 0);
            });
        }
    }
}
