namespace RoslynCodeLens.Models;

public record GetOverloadsResult(
    string ContainingType,
    IReadOnlyList<OverloadInfo> Overloads);
