namespace RoslynCodeLens.Models;

public record FindAsyncViolationsResult(
    AsyncViolationSummary Summary,
    IReadOnlyList<AsyncViolation> Violations);
