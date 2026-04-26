namespace RoslynCodeLens.Models;

public record FindTestsForSymbolResult(
    string Symbol,
    IReadOnlyList<TestReference> DirectTests,
    IReadOnlyList<TestReference> TransitiveTests);
