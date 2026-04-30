namespace RoslynCodeLens.Models;

public record ProjectHealthHotspots(
    IReadOnlyList<ComplexityMetric> Complexity,
    IReadOnlyList<LargeClassInfo> LargeClasses,
    IReadOnlyList<NamingViolation> Naming,
    IReadOnlyList<UnusedSymbolInfo> Unused,
    IReadOnlyList<ReflectionUsage> Reflection,
    IReadOnlyList<AsyncViolation> Async,
    IReadOnlyList<DisposableMisuseViolation> Disposable);
