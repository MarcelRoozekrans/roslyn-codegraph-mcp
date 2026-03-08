namespace RoslynCodeGraph.Models;

public record CircularDependency(string Level, IReadOnlyList<string> Cycle);
