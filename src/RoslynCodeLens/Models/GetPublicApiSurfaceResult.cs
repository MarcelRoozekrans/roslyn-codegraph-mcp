namespace RoslynCodeLens.Models;

public record GetPublicApiSurfaceResult(
    PublicApiSummary Summary,
    IReadOnlyList<PublicApiEntry> Entries);
