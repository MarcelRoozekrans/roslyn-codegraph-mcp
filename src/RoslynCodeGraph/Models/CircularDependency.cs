namespace RoslynCodeGraph.Models;

public record CircularDependency(string Level, List<string> Cycle);
