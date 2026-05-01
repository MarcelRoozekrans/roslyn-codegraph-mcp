using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.TestDiscovery;

[Collection("TestSolution")]
public class TestMethodClassifierTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public TestMethodClassifierTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void Classify_XUnitFactMethod_ReturnsXUnit()
    {
        var method = FindMethod("XUnitFixture.SampleTests", "DirectGreetTest");
        var classification = TestMethodClassifier.Classify(method);

        Assert.NotNull(classification);
        Assert.Equal(TestFramework.XUnit, classification!.Framework);
        Assert.Equal("Fact", classification.AttributeShortName);
    }

    [Fact]
    public void Classify_NUnitTestMethod_ReturnsNUnit()
    {
        var method = FindMethod("NUnitFixture.SampleTests", "DirectGreetTest");
        var classification = TestMethodClassifier.Classify(method);

        Assert.NotNull(classification);
        Assert.Equal(TestFramework.NUnit, classification!.Framework);
    }

    [Fact]
    public void Classify_MSTestMethod_ReturnsMSTest()
    {
        var method = FindMethod("MSTestFixture.SampleTests", "DirectGreetTest");
        var classification = TestMethodClassifier.Classify(method);

        Assert.NotNull(classification);
        Assert.Equal(TestFramework.MSTest, classification!.Framework);
    }

    [Fact]
    public void Classify_NonTestMethod_ReturnsNull()
    {
        var method = FindMethod("TestLib.Greeter", "Greet");
        Assert.Null(TestMethodClassifier.Classify(method));
    }

    [Fact]
    public void IsTestMethod_TestMethod_ReturnsTrue()
    {
        var method = FindMethod("XUnitFixture.SampleTests", "DirectGreetTest");
        Assert.True(TestMethodClassifier.IsTestMethod(method));
    }

    [Fact]
    public void IsTestMethod_NonTestMethod_ReturnsFalse()
    {
        var method = FindMethod("TestLib.Greeter", "Greet");
        Assert.False(TestMethodClassifier.IsTestMethod(method));
    }

    private IMethodSymbol FindMethod(string typeName, string methodName)
    {
        var methods = _resolver.FindMethods($"{typeName}.{methodName}");
        Assert.NotEmpty(methods);
        return methods[0];
    }
}
