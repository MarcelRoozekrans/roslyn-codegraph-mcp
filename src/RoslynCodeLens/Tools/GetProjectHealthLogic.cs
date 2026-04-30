using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class GetProjectHealthLogic
{
    public static GetProjectHealthResult Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string? project,
        int hotspotsPerDimension)
    {
        var complexity = GetComplexityMetricsLogic.Execute(loaded, resolver, project, threshold: 10);
        var largeClasses = FindLargeClassesLogic.Execute(loaded, resolver, project, maxMembers: 20, maxLines: 500);
        var naming = FindNamingViolationsLogic.Execute(loaded, resolver, project);
        var unused = FindUnusedSymbolsLogic.Execute(loaded, resolver, project, includeInternal: false);
        var reflection = FindReflectionUsageLogic.Execute(loaded, resolver, symbol: null);
        var async = FindAsyncViolationsLogic.Execute(loaded, resolver).Violations;
        var disposable = FindDisposableMisuseLogic.Execute(loaded, resolver).Violations;

        var fileToProject = BuildFileToProjectMap(loaded);
        var reflectionWithProject = reflection
            .Select(r => (Usage: r, Project: fileToProject.TryGetValue(r.File, out var p) ? p : ""))
            .ToList();

        // Outer filter is load-bearing: 5 of the 7 underlying tools (complexity, large classes,
        // naming, unused, reflection) don't filter test projects internally — only async-violations
        // and disposable-misuse do. Removing this would leak test projects into the result.
        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);
        var productionProjects = loaded.Solution.Projects
            .Where(p => !testProjectIds.Contains(p.Id))
            .Select(p => p.Name)
            .Where(name => project is null || string.Equals(name, project, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var entries = new List<ProjectHealth>(productionProjects.Count);
        foreach (var projectName in productionProjects)
        {
            var pComplexity = complexity.Where(c => string.Equals(c.Project, projectName, StringComparison.Ordinal)).ToList();
            var pLarge = largeClasses.Where(c => string.Equals(c.Project, projectName, StringComparison.Ordinal)).ToList();
            var pNaming = naming.Where(n => string.Equals(n.Project, projectName, StringComparison.Ordinal)).ToList();
            var pUnused = unused.Where(u => string.Equals(u.Project, projectName, StringComparison.Ordinal)).ToList();
            var pReflection = reflectionWithProject
                .Where(r => string.Equals(r.Project, projectName, StringComparison.Ordinal))
                .Select(r => r.Usage)
                .ToList();
            var pAsync = async.Where(a => string.Equals(a.Project, projectName, StringComparison.Ordinal)).ToList();
            var pDisposable = disposable.Where(d => string.Equals(d.Project, projectName, StringComparison.Ordinal)).ToList();

            var counts = new ProjectHealthCounts(
                ComplexityHotspots: pComplexity.Count,
                LargeClasses: pLarge.Count,
                NamingViolations: pNaming.Count,
                UnusedSymbols: pUnused.Count,
                ReflectionUsages: pReflection.Count,
                AsyncViolations: pAsync.Count,
                DisposableMisuse: pDisposable.Count);

            var n = Math.Max(0, hotspotsPerDimension);
            var hotspots = new ProjectHealthHotspots(
                Complexity: pComplexity.OrderByDescending(c => c.Complexity).Take(n).ToList(),
                LargeClasses: pLarge.OrderByDescending(c => c.LineCount).Take(n).ToList(),
                Naming: pNaming.Take(n).ToList(),
                Unused: pUnused.Take(n).ToList(),
                Reflection: pReflection.Take(n).ToList(),
                Async: pAsync
                    .OrderByDescending(a => (int)a.Severity)
                    .ThenBy(a => a.FilePath, StringComparer.Ordinal)
                    .ThenBy(a => a.Line)
                    .Take(n)
                    .ToList(),
                Disposable: pDisposable
                    .OrderByDescending(d => (int)d.Severity)
                    .ThenBy(d => d.FilePath, StringComparer.Ordinal)
                    .ThenBy(d => d.Line)
                    .Take(n)
                    .ToList());

            entries.Add(new ProjectHealth(projectName, counts, hotspots));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Project, b.Project));
        return new GetProjectHealthResult(entries);
    }

    private static Dictionary<string, string> BuildFileToProjectMap(LoadedSolution loaded)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in loaded.Solution.Projects)
        {
            foreach (var doc in p.Documents)
            {
                if (!string.IsNullOrEmpty(doc.FilePath))
                    map[doc.FilePath] = p.Name;
            }
        }
        return map;
    }
}
