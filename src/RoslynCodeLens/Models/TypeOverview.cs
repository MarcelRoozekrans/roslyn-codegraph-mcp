namespace RoslynCodeLens.Models;

public record TypeOverview(
    SymbolContext? Context,
    TypeHierarchy? Hierarchy,
    IReadOnlyList<DiagnosticInfo> Diagnostics,
    SymbolOrigin? Origin = null);
