namespace RoslynCodeLens.Models;

public record CallGraphEdge(
    string Target,
    CallGraphEdgeKind EdgeKind);
