namespace RoslynCodeLens.Models;

public record ProjectDependencyGraph(
    IReadOnlyList<ProjectRef> Direct,
    IReadOnlyList<ProjectRef> Transitive);
