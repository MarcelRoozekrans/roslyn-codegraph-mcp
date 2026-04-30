namespace RoslynCodeLens.Models;

public record ProjectHealth(
    string Project,
    ProjectHealthCounts Counts,
    ProjectHealthHotspots Hotspots);
