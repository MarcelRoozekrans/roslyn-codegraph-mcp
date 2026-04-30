namespace RoslynCodeLens.Models;

public record GetProjectHealthResult(
    IReadOnlyList<ProjectHealth> Projects);
