using Microsoft.CodeAnalysis;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class GetPublicApiSurfaceLogic
{
    public static GetPublicApiSurfaceResult Execute(LoadedSolution loaded, SymbolResolver source)
    {
        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);
        var entries = new List<PublicApiEntry>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            if (testProjectIds.Contains(projectId)) continue;

            var projectName = source.GetProjectName(projectId);
            entries.AddRange(PublicApiSurfaceExtractor.Extract(
                compilation.Assembly,
                projectName,
                requireSourceLocation: true));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var byKind = entries
            .GroupBy(e => e.Kind.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byProject = entries
            .GroupBy(e => e.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byAccessibility = entries
            .GroupBy(e => e.Accessibility.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var summary = new PublicApiSummary(
            TotalEntries: entries.Count,
            ByKind: byKind,
            ByProject: byProject,
            ByAccessibility: byAccessibility);

        return new GetPublicApiSurfaceResult(summary, entries);
    }
}
