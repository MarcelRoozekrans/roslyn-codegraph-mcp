using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tests.TestDiscovery;

public class TestAttributeRecognizerTests
{
    [Theory]
    [InlineData("Xunit", "FactAttribute", TestFramework.XUnit)]
    [InlineData("Xunit", "TheoryAttribute", TestFramework.XUnit)]
    [InlineData("NUnit.Framework", "TestAttribute", TestFramework.NUnit)]
    [InlineData("NUnit.Framework", "TestCaseAttribute", TestFramework.NUnit)]
    [InlineData("NUnit.Framework", "TestCaseSourceAttribute", TestFramework.NUnit)]
    [InlineData("Microsoft.VisualStudio.TestTools.UnitTesting", "TestMethodAttribute", TestFramework.MSTest)]
    [InlineData("Microsoft.VisualStudio.TestTools.UnitTesting", "DataTestMethodAttribute", TestFramework.MSTest)]
    public void Recognize_KnownAttribute_ReturnsFramework(string ns, string name, TestFramework expected)
    {
        Assert.Equal(expected, TestAttributeRecognizer.Recognize(ns, name));
    }

    [Theory]
    [InlineData("System", "ObsoleteAttribute")]
    [InlineData("Xunit", "FactSomethingElse")]
    [InlineData("NotARealNamespace", "TestAttribute")]
    public void Recognize_UnknownAttribute_ReturnsNull(string ns, string name)
    {
        Assert.Null(TestAttributeRecognizer.Recognize(ns, name));
    }
}
