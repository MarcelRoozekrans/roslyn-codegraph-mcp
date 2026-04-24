namespace RoslynCodeLens.Models;

public record ExternalNamespaceSummary(
    string Namespace,
    int TypeCount,
    IReadOnlyList<string> PublicTypeNames);
