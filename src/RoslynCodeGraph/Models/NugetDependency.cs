namespace RoslynCodeGraph.Models;

public record NugetDependency(string PackageName, string Version, string Project);

public record NugetDependencyGraph(List<NugetDependency> Packages);
