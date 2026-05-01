namespace RoslynCodeLens.Models;

public record ObsoleteSymbolGroup(
    string SymbolName,
    string DeprecationMessage,
    bool IsError,
    int UsageCount,
    IReadOnlyList<ObsoleteUsageSite> Usages);
