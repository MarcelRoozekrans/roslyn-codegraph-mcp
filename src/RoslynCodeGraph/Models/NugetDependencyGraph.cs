namespace RoslynCodeGraph.Models;

public record NugetDependencyGraph(IReadOnlyList<NugetDependency> Packages);
