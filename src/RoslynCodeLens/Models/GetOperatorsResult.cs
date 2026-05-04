namespace RoslynCodeLens.Models;

public record GetOperatorsResult(
    string ContainingType,
    IReadOnlyList<OperatorInfo> Operators);
