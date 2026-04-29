namespace RoslynCodeLens.Models;

public record FindBreakingChangesResult(
    BreakingChangesSummary Summary,
    IReadOnlyList<BreakingChange> Changes);
