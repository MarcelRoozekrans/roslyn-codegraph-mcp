namespace RoslynCodeLens.Models;

public record NugetDependencyGraph(IReadOnlyList<NugetDependency> Packages);
