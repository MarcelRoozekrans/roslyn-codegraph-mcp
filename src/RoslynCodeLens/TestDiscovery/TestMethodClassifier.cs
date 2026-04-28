using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.TestDiscovery;

public record TestMethodClassification(TestFramework Framework, string AttributeShortName);

public static class TestMethodClassifier
{
    public static TestMethodClassification? Classify(IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
        {
            var ns = attr.AttributeClass?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            var name = attr.AttributeClass?.Name ?? string.Empty;
            var framework = TestAttributeRecognizer.Recognize(ns, name);
            if (framework is not null)
            {
                var attributeShortName = name.EndsWith("Attribute", StringComparison.Ordinal)
                    ? name[..^"Attribute".Length]
                    : name;
                return new TestMethodClassification(framework.Value, attributeShortName);
            }
        }
        return null;
    }

    public static bool IsTestMethod(IMethodSymbol method) => Classify(method) is not null;
}
