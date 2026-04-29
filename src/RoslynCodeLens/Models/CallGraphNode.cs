namespace RoslynCodeLens.Models;

public record CallGraphNode(
    CallGraphNodeKind Kind,
    string Project,
    string FilePath,
    int Line,
    bool IsExternal,
    IReadOnlyList<CallGraphEdge> Edges);
