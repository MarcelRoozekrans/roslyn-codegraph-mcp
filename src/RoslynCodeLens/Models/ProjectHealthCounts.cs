namespace RoslynCodeLens.Models;

public record ProjectHealthCounts(
    int ComplexityHotspots,
    int LargeClasses,
    int NamingViolations,
    int UnusedSymbols,
    int ReflectionUsages,
    int AsyncViolations,
    int DisposableMisuse);
