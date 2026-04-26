using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Models;

public record TestReference(
    string FullyQualifiedName,
    TestFramework Framework,
    string Attribute,
    string FilePath,
    int Line,
    string Project,
    IReadOnlyList<string>? CallChain = null);
