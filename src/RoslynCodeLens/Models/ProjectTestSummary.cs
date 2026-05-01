namespace RoslynCodeLens.Models;

public record ProjectTestSummary(
    string Project,
    int TotalTests,
    IReadOnlyDictionary<string, int> ByFramework,
    IReadOnlyDictionary<string, int> ByAttribute,
    IReadOnlyList<TestMethodSummary> Tests);
