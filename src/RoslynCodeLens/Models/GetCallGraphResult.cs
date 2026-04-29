namespace RoslynCodeLens.Models;

public record GetCallGraphResult(
    string Root,
    string Direction,
    int MaxDepthRequested,
    bool Truncated,
    IReadOnlyDictionary<string, CallGraphNode> Callees,
    IReadOnlyDictionary<string, CallGraphNode> Callers);
