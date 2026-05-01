namespace RoslynCodeLens.Models;

public record FindObsoleteUsageResult(
    IReadOnlyList<ObsoleteSymbolGroup> Groups);
