namespace RoslynCodeLens.Models;

public record ExternalAssemblyOverview(
    string Mode,                                             // "summary" | "namespace"
    string Name,
    string Version,
    string? TargetFramework,
    string? PublicKeyToken,
    IReadOnlyList<ExternalNamespaceSummary> NamespaceTree,   // non-empty for mode=="summary"
    IReadOnlyList<ExternalTypeInfo> Types);                  // non-empty for mode=="namespace"
