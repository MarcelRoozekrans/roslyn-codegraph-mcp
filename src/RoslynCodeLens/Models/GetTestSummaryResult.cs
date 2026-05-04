namespace RoslynCodeLens.Models;

public record GetTestSummaryResult(
    IReadOnlyList<ProjectTestSummary> Projects);
