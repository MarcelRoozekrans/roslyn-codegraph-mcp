namespace RoslynCodeLens.Models;

public record DataFlowInfo(
    IReadOnlyList<string> Declared,
    IReadOnlyList<string> Read,
    IReadOnlyList<string> Written,
    IReadOnlyList<string> AlwaysAssigned,
    IReadOnlyList<string> Captured,
    IReadOnlyList<string> DataFlowsIn,
    IReadOnlyList<string> DataFlowsOut);
