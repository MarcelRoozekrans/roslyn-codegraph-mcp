# `get_project_health` Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a new MCP tool `get_project_health` that aggregates 7 health dimensions (complexity, large classes, naming, unused, reflection, async violations, disposable misuse) per project. Returns counts plus top-N hotspots inline so an agent can answer "how is this project doing?" in a single call.

**Architecture:** Composite tool. Calls each underlying `*Logic.Execute` once with the project filter where supported, groups results by project name, computes counts, trims to top-N hotspots after sorting by severity proxy. Reflection usage (no `Project` field) gets project derived from file path via `Solution.GetDocumentIdsWithFilePath`.

**Tech Stack:** Roslyn (`Microsoft.CodeAnalysis`), xUnit, BenchmarkDotNet, ModelContextProtocol.Server.

**Reference design:** `docs/plans/2026-04-30-get-project-health-design.md`

**Patterns to mirror:**
- Composite tool pattern: `src/RoslynCodeLens/Tools/GetTypeOverviewTool.cs` (compound that bundles existing logic)
- Whole-solution audit pattern: `src/RoslynCodeLens/Tools/FindUncoveredSymbolsTool.cs`
- MCP auto-registration: `Program.cs:35` uses `WithToolsFromAssembly()` — no edit needed.

---

## Task 1: Models

5 small files: 3 records + the result wrapper + a helper enum-free shape.

**Files:**
- Create: `src/RoslynCodeLens/Models/ProjectHealthCounts.cs`
- Create: `src/RoslynCodeLens/Models/ProjectHealthHotspots.cs`
- Create: `src/RoslynCodeLens/Models/ProjectHealth.cs`
- Create: `src/RoslynCodeLens/Models/GetProjectHealthResult.cs`

**Step 1: `ProjectHealthCounts.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record ProjectHealthCounts(
    int ComplexityHotspots,
    int LargeClasses,
    int NamingViolations,
    int UnusedSymbols,
    int ReflectionUsages,
    int AsyncViolations,
    int DisposableMisuse);
```

**Step 2: `ProjectHealthHotspots.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record ProjectHealthHotspots(
    IReadOnlyList<ComplexityMetric> Complexity,
    IReadOnlyList<LargeClassInfo> LargeClasses,
    IReadOnlyList<NamingViolation> Naming,
    IReadOnlyList<UnusedSymbolInfo> Unused,
    IReadOnlyList<ReflectionUsage> Reflection,
    IReadOnlyList<AsyncViolation> Async,
    IReadOnlyList<DisposableMisuseViolation> Disposable);
```

**Step 3: `ProjectHealth.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record ProjectHealth(
    string Project,
    ProjectHealthCounts Counts,
    ProjectHealthHotspots Hotspots);
```

**Step 4: `GetProjectHealthResult.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record GetProjectHealthResult(
    IReadOnlyList<ProjectHealth> Projects);
```

**Step 5: Build**

```bash
dotnet build src/RoslynCodeLens
```

Expected: 0 errors.

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Models/ProjectHealthCounts.cs \
  src/RoslynCodeLens/Models/ProjectHealthHotspots.cs \
  src/RoslynCodeLens/Models/ProjectHealth.cs \
  src/RoslynCodeLens/Models/GetProjectHealthResult.cs
git commit -m "feat: add models for get_project_health"
```

---

## Task 2: `GetProjectHealthLogic` with comprehensive tests (TDD)

The composite engine. Calls all 7 underlying tools, groups by project, counts and trims.

**Files:**
- Create: `src/RoslynCodeLens/Tools/GetProjectHealthLogic.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/GetProjectHealthToolTests.cs`

**Step 1: Write the failing tests**

`tests/RoslynCodeLens.Tests/Tools/GetProjectHealthToolTests.cs`:

```csharp
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GetProjectHealthToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Result_HasOneEntryPerProductionProject()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: null, hotspotsPerDimension: 5);

        // TestLib and TestLib2 are production; everything else is a fixture/test project.
        Assert.Contains(result.Projects, p => p.Project == "TestLib");
        Assert.Contains(result.Projects, p => p.Project == "TestLib2");
    }

    [Fact]
    public void TestProjectsAreSkipped()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: null, hotspotsPerDimension: 5);

        // Test project detection — these names belong to test/fixture projects.
        Assert.DoesNotContain(result.Projects, p => p.Project == "RoslynCodeLens.Tests");
    }

    [Fact]
    public void Counts_ComplexityMatchesUnderlyingTool()
    {
        var direct = GetComplexityMetricsLogic.Execute(_loaded, _resolver, "TestLib", 10);
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "TestLib", hotspotsPerDimension: 5);

        var entry = Assert.Single(result.Projects);
        Assert.Equal(direct.Count, entry.Counts.ComplexityHotspots);
    }

    [Fact]
    public void Counts_LargeClassesMatchesUnderlyingTool()
    {
        var direct = FindLargeClassesLogic.Execute(_loaded, _resolver, "TestLib", 20, 500);
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "TestLib", hotspotsPerDimension: 5);

        var entry = Assert.Single(result.Projects);
        Assert.Equal(direct.Count, entry.Counts.LargeClasses);
    }

    [Fact]
    public void Counts_NamingViolationsMatchesUnderlyingTool()
    {
        var direct = FindNamingViolationsLogic.Execute(_loaded, _resolver, "TestLib");
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "TestLib", hotspotsPerDimension: 5);

        var entry = Assert.Single(result.Projects);
        Assert.Equal(direct.Count, entry.Counts.NamingViolations);
    }

    [Fact]
    public void Counts_UnusedSymbolsMatchesUnderlyingTool()
    {
        var direct = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "TestLib", hotspotsPerDimension: 5);

        var entry = Assert.Single(result.Projects);
        Assert.Equal(direct.Count, entry.Counts.UnusedSymbols);
    }

    [Fact]
    public void Hotspots_TrimmedToRequestedSize()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: null, hotspotsPerDimension: 2);

        foreach (var p in result.Projects)
        {
            Assert.True(p.Hotspots.Complexity.Count <= 2);
            Assert.True(p.Hotspots.LargeClasses.Count <= 2);
            Assert.True(p.Hotspots.Naming.Count <= 2);
            Assert.True(p.Hotspots.Unused.Count <= 2);
            Assert.True(p.Hotspots.Reflection.Count <= 2);
            Assert.True(p.Hotspots.Async.Count <= 2);
            Assert.True(p.Hotspots.Disposable.Count <= 2);
        }
    }

    [Fact]
    public void ProjectFilter_OnlyReturnsRequestedProject()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "TestLib", hotspotsPerDimension: 5);

        var entry = Assert.Single(result.Projects);
        Assert.Equal("TestLib", entry.Project);
    }

    [Fact]
    public void Hotspots_Complexity_SortedByCyclomaticDesc()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: null, hotspotsPerDimension: 10);

        foreach (var p in result.Projects)
        {
            for (int i = 1; i < p.Hotspots.Complexity.Count; i++)
            {
                Assert.True(
                    p.Hotspots.Complexity[i - 1].Complexity >= p.Hotspots.Complexity[i].Complexity,
                    $"Complexity hotspots not sorted desc in {p.Project} at index {i}");
            }
        }
    }

    [Fact]
    public void Hotspots_LargeClasses_SortedByLineCountDesc()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: null, hotspotsPerDimension: 10);

        foreach (var p in result.Projects)
        {
            for (int i = 1; i < p.Hotspots.LargeClasses.Count; i++)
            {
                Assert.True(
                    p.Hotspots.LargeClasses[i - 1].LineCount >= p.Hotspots.LargeClasses[i].LineCount,
                    $"LargeClasses hotspots not sorted desc in {p.Project} at index {i}");
            }
        }
    }

    [Fact]
    public void EmptyHotspotsRequest_StillPopulatesCounts()
    {
        // hotspotsPerDimension=0 → counts should still reflect underlying tool totals,
        // but hotspot lists should be empty.
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "TestLib", hotspotsPerDimension: 0);
        var entry = Assert.Single(result.Projects);

        Assert.Empty(entry.Hotspots.Complexity);
        Assert.Empty(entry.Hotspots.LargeClasses);
        Assert.Empty(entry.Hotspots.Naming);
        Assert.Empty(entry.Hotspots.Unused);
        Assert.Empty(entry.Hotspots.Reflection);
        Assert.Empty(entry.Hotspots.Async);
        Assert.Empty(entry.Hotspots.Disposable);

        // At least one count should be > 0 in TestLib (it has known complexity hotspots etc.)
        var totalCount = entry.Counts.ComplexityHotspots + entry.Counts.LargeClasses
                       + entry.Counts.NamingViolations + entry.Counts.UnusedSymbols
                       + entry.Counts.ReflectionUsages + entry.Counts.AsyncViolations
                       + entry.Counts.DisposableMisuse;
        Assert.True(totalCount > 0, "Expected TestLib to have at least one finding across all dimensions");
    }

    [Fact]
    public void UnknownProject_ReturnsEmptyList()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: "DoesNotExist", hotspotsPerDimension: 5);

        Assert.Empty(result.Projects);
    }

    [Fact]
    public void ProjectsSorted_AscendingByName()
    {
        var result = GetProjectHealthLogic.Execute(_loaded, _resolver, project: null, hotspotsPerDimension: 5);

        for (int i = 1; i < result.Projects.Count; i++)
        {
            Assert.True(
                string.CompareOrdinal(result.Projects[i - 1].Project, result.Projects[i].Project) <= 0,
                $"Project sort violation at index {i}");
        }
    }
}
```

**Step 2: Run to verify they fail**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetProjectHealthToolTests"
```

Expect compile error: `GetProjectHealthLogic` doesn't exist.

**Step 3: Create `src/RoslynCodeLens/Tools/GetProjectHealthLogic.cs`**

```csharp
using Microsoft.CodeAnalysis;
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
        // Run all 7 underlying tools sequentially. Each accepts the project filter where it supports one;
        // others (FindAsyncViolations, FindDisposableMisuse, FindReflectionUsage) are filtered client-side.
        var complexity = GetComplexityMetricsLogic.Execute(loaded, resolver, project, threshold: 10);
        var largeClasses = FindLargeClassesLogic.Execute(loaded, resolver, project, maxMembers: 20, maxLines: 500);
        var naming = FindNamingViolationsLogic.Execute(loaded, resolver, project);
        var unused = FindUnusedSymbolsLogic.Execute(loaded, resolver, project, includeInternal: false);
        var reflection = FindReflectionUsageLogic.Execute(loaded, resolver, symbol: null);
        var async = FindAsyncViolationsLogic.Execute(loaded, resolver).Violations;
        var disposable = FindDisposableMisuseLogic.Execute(loaded, resolver).Violations;

        // Reflection has no Project field — derive it via Solution.GetDocumentIdsWithFilePath.
        var fileToProject = BuildFileToProjectMap(loaded);
        var reflectionWithProject = reflection
            .Select(r => (Usage: r, Project: fileToProject.TryGetValue(r.File, out var p) ? p : ""))
            .ToList();

        // Determine the project set.
        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);
        var productionProjects = loaded.Solution.Projects
            .Where(p => !testProjectIds.Contains(p.Id))
            .Select(p => p.Name)
            .Where(name => project is null || string.Equals(name, project, StringComparison.Ordinal))
            .ToList();

        var entries = new List<ProjectHealth>(productionProjects.Count);
        foreach (var projectName in productionProjects)
        {
            var pComplexity = complexity.Where(c => c.Project == projectName).ToList();
            var pLarge = largeClasses.Where(c => c.Project == projectName).ToList();
            var pNaming = naming.Where(n => n.Project == projectName).ToList();
            var pUnused = unused.Where(u => u.Project == projectName).ToList();
            var pReflection = reflectionWithProject.Where(r => r.Project == projectName).Select(r => r.Usage).ToList();
            var pAsync = async.Where(a => a.Project == projectName).ToList();
            var pDisposable = disposable.Where(d => d.Project == projectName).ToList();

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
                Async: pAsync.OrderByDescending(a => (int)a.Severity).ThenBy(a => a.FilePath, StringComparer.Ordinal).ThenBy(a => a.Line).Take(n).ToList(),
                Disposable: pDisposable.OrderByDescending(d => (int)d.Severity).ThenBy(d => d.FilePath, StringComparer.Ordinal).ThenBy(d => d.Line).Take(n).ToList());

            entries.Add(new ProjectHealth(projectName, counts, hotspots));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Project, b.Project));
        return new GetProjectHealthResult(entries);
    }

    private static Dictionary<string, string> BuildFileToProjectMap(LoadedSolution loaded)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in loaded.Solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (!string.IsNullOrEmpty(doc.FilePath))
                    map[doc.FilePath] = project.Name;
            }
        }
        return map;
    }
}
```

**Step 4: Run the tests**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetProjectHealthToolTests" -v normal
```

Expect 13/13 pass.

**Common debugging:**
- If `Counts_*MatchesUnderlyingTool` fails: the underlying tool's project filter and our client-side filter must produce identical results. Each direct call uses `project: "TestLib"` — the composite passes the same. They should match exactly.
- If `Hotspots_TrimmedToRequestedSize` fails for `Async`/`Disposable`: those tools don't accept project filter so we filter `Violations` by `.Project == projectName` — verify the violation's `Project` field is populated.
- If a project shows up unexpectedly: `TestProjectDetector.GetTestProjectIds` should exclude it. Confirm with `_loaded.Solution.Projects.Select(p => p.Name)`.
- If `Hotspots_Complexity_SortedByCyclomaticDesc` fails: the underlying `GetComplexityMetricsLogic` may already sort one way; we re-sort by `Complexity` desc explicitly.

**Step 5: Run full suite (sanity)**

```bash
dotnet test
```

Pre-existing flakiness on metadata-resolution tests is fine; our new tests must all pass.

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Tools/GetProjectHealthLogic.cs \
  tests/RoslynCodeLens.Tests/Tools/GetProjectHealthToolTests.cs
git commit -m "feat: add GetProjectHealthLogic compositing 7 health dimensions"
```

---

## Task 3: `GetProjectHealthTool` MCP wrapper

**Files:**
- Create: `src/RoslynCodeLens/Tools/GetProjectHealthTool.cs`

**Step 1: Create the wrapper**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetProjectHealthTool
{
    [McpServerTool(Name = "get_project_health")]
    [Description(
        "Aggregate 7 health dimensions per project in one call: complexity hotspots, " +
        "large classes, naming violations, unused symbols, reflection usage, async " +
        "violations, and disposable misuse. Returns counts per dimension plus the top-N " +
        "hotspots inline (default 5) so the caller can prioritise without follow-up calls. " +
        "Use this when answering 'how is this project doing?' / 'where should I focus?' / " +
        "'what's the technical debt picture?'. " +
        "Test projects are skipped. Sort: projects ASC by name; hotspots sorted by severity " +
        "proxy per dimension (cyclomatic complexity desc, line count desc, severity enum desc " +
        "for async/disposable).")]
    public static GetProjectHealthResult Execute(
        MultiSolutionManager manager,
        [Description("Optional: restrict to a single project by name. Default: whole solution, grouped per project.")]
        string? project = null,
        [Description("How many hotspots to include per dimension. Default: 5. Pass 0 for counts-only output.")]
        int hotspotsPerDimension = 5)
    {
        manager.EnsureLoaded();
        return GetProjectHealthLogic.Execute(
            manager.GetLoadedSolution(),
            manager.GetResolver(),
            project,
            hotspotsPerDimension);
    }
}
```

**Step 2: Build whole solution**

```bash
dotnet build
```

Expected: 0 errors. Auto-registration via `Program.cs:35`.

**Step 3: Run targeted tests**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetProjectHealthToolTests"
```

Expect 13/13 pass.

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Tools/GetProjectHealthTool.cs
git commit -m "feat: register get_project_health MCP tool"
```

---

## Task 4: Add benchmark

**Files:**
- Modify: `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

**Step 1:** Find the existing `find_unused_symbols: all` benchmark and add the new one immediately after.

```csharp
[Benchmark(Description = "get_project_health: whole solution")]
public object GetProjectHealth()
{
    return GetProjectHealthLogic.Execute(_loaded, _resolver, project: null, hotspotsPerDimension: 5);
}
```

**Step 2: Build the benchmarks project**

```bash
dotnet build benchmarks/RoslynCodeLens.Benchmarks -c Release
```

Expected: 0 errors.

**Step 3: Commit**

```bash
git add benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs
git commit -m "bench: add get_project_health benchmark"
```

---

## Task 5: Update SKILL.md, README, CLAUDE.md

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
- Modify: `README.md`
- Modify: `CLAUDE.md`

**Step 1: SKILL.md — Red Flags routing table**

Add near the existing complexity / naming entries:

```
| "How is this project doing?" / "Where should I focus?" / "Show me the technical debt picture" | `get_project_health` |
```

**Step 2: SKILL.md — Quick Reference table**

Add near `get_complexity_metrics`:

```
| `get_project_health` | "How is this project doing?" / "Top hotspots across all dimensions" |
```

**Step 3: SKILL.md — Understanding-a-Codebase section**

Add as a new bullet:

```
- `get_project_health` — Composite audit: complexity, large classes, naming, unused, reflection, async violations, disposable misuse — counts + top-N hotspots per dimension, per project. Use when you'd otherwise call 7 separate audit tools.
```

**Step 4: README.md Features list**

Add near the audit/quality tools:

```
- **get_project_health** — Composite audit aggregating 7 quality dimensions per project (complexity, large classes, naming, unused symbols, reflection, async violations, disposable misuse) with counts and top-N hotspots inline.
```

**Step 5: CLAUDE.md — bump tool count**

Find the line that says "30 code intelligence tools" (or whatever the current number is) and bump by 1.

```bash
grep -n "code intelligence tools" CLAUDE.md
```

**Step 6: Sanity check**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetProjectHealthToolTests"
```

Expect 13/13 pass.

**Step 7: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md \
  README.md \
  CLAUDE.md
git commit -m "docs: announce get_project_health in SKILL.md, README, CLAUDE.md"
```

---

## Done

After Task 5 the branch should have ~7 commits (design + plan + 5 implementation tasks), all `GetProjectHealthToolTests` green, the benchmark project compiling, and the tool auto-registered. From there: `/superpowers:requesting-code-review` → PR.
