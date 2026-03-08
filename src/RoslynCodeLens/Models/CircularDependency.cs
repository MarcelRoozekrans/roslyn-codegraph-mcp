namespace RoslynCodeLens.Models;

public record CircularDependency(string Level, IReadOnlyList<string> Cycle);
