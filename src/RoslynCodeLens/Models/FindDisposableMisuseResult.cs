namespace RoslynCodeLens.Models;

public record FindDisposableMisuseResult(
    DisposableMisuseSummary Summary,
    IReadOnlyList<DisposableMisuseViolation> Violations);
