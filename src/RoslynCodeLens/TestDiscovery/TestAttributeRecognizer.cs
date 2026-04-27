namespace RoslynCodeLens.TestDiscovery;

public static class TestAttributeRecognizer
{
    private static readonly Dictionary<(string Namespace, string Name), TestFramework> Map = new()
    {
        [("Xunit", "FactAttribute")] = TestFramework.XUnit,
        [("Xunit", "TheoryAttribute")] = TestFramework.XUnit,
        [("NUnit.Framework", "TestAttribute")] = TestFramework.NUnit,
        [("NUnit.Framework", "TestCaseAttribute")] = TestFramework.NUnit,
        [("NUnit.Framework", "TestCaseSourceAttribute")] = TestFramework.NUnit,
        [("Microsoft.VisualStudio.TestTools.UnitTesting", "TestMethodAttribute")] = TestFramework.MSTest,
        [("Microsoft.VisualStudio.TestTools.UnitTesting", "DataTestMethodAttribute")] = TestFramework.MSTest,
    };

    public static TestFramework? Recognize(string namespaceFullName, string attributeName)
        => Map.TryGetValue((namespaceFullName, attributeName), out var framework) ? framework : null;
}
