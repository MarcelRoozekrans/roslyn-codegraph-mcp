namespace RoslynCodeLens.Models;

public record CoverageSummary(
    int TotalSymbols,
    int CoveredCount,
    int UncoveredCount,
    int CoveragePercent,
    int RiskHotspotCount);
