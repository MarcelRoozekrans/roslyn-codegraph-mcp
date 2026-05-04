using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tests.TestDiscovery;

[Collection("TestSolution")]
public class TestFrameworkDetectorTests
{
    private readonly TestSolutionFixture _fixture;

    public TestFrameworkDetectorTests(TestSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void DetectFramework_XUnitProject_ReturnsXUnit()
    {
        var project = _fixture.Loaded.Solution.Projects
            .Single(p => p.Name == "XUnitFixture");

        var framework = TestFrameworkDetector.DetectFramework(project);

        Assert.Equal(TestFramework.XUnit, framework);
    }

    [Fact]
    public void DetectFramework_NUnitProject_ReturnsNUnit()
    {
        var project = _fixture.Loaded.Solution.Projects
            .Single(p => p.Name == "NUnitFixture");

        var framework = TestFrameworkDetector.DetectFramework(project);

        Assert.Equal(TestFramework.NUnit, framework);
    }

    [Fact]
    public void DetectFramework_MSTestProject_ReturnsMSTest()
    {
        var project = _fixture.Loaded.Solution.Projects
            .Single(p => p.Name == "MSTestFixture");

        var framework = TestFrameworkDetector.DetectFramework(project);

        Assert.Equal(TestFramework.MSTest, framework);
    }

    [Fact]
    public void DetectFramework_ProductionProject_ReturnsNull()
    {
        var project = _fixture.Loaded.Solution.Projects
            .Single(p => p.Name == "TestLib");

        var framework = TestFrameworkDetector.DetectFramework(project);

        Assert.Null(framework);
    }
}
