using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GenerateTestSkeletonToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GenerateTestSkeletonToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void Method_GeneratesFactSkeletonForVoidMethod()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.Greeter.Dispose",
            framework: "xunit");

        Assert.Equal("XUnit", result.Framework);
        Assert.Equal("GreeterTests", result.ClassName);
        Assert.Contains("[Fact]", result.Code);
        Assert.Contains("public void Dispose_HappyPath()", result.Code);
        Assert.Contains("var sut = new Greeter", result.Code);
        Assert.Contains("using Xunit;", result.Code);
        Assert.Contains("namespace TestLib.Tests", result.Code);
    }

    [Fact]
    public void Type_GeneratesClassWithFactPerPublicMethod()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.Greeter",
            framework: "xunit");

        Assert.Equal("GreeterTests", result.ClassName);
        // Greeter has Dispose() — a no-arg void; should appear as a happy-path Fact.
        Assert.Contains("public void Dispose_HappyPath()", result.Code);
    }

    [Fact]
    public void StaticMethod_DoesNotInstantiateSut()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.StaticHelper.Compute",
            framework: "xunit");

        Assert.DoesNotContain("var sut = new", result.Code);
        Assert.Contains("StaticHelper.Compute()", result.Code);
    }

    [Fact]
    public void MethodReturningTask_GeneratesAsyncTest()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.AsyncWorker.DoAsync",
            framework: "xunit");

        Assert.Contains("public async Task DoAsync_HappyPath()", result.Code);
        Assert.Contains("await sut.DoAsync()", result.Code);
        Assert.Contains("using System.Threading.Tasks;", result.Code);
    }

    [Fact]
    public void MethodWithPrimitiveParams_GeneratesTheoryWithInlineData()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.Calculator.Add",
            framework: "xunit");

        Assert.Contains("[Theory]", result.Code);
        Assert.Contains("[InlineData(", result.Code);
        Assert.Contains("public void Add_Theory(int a, int b)", result.Code);
    }
}
