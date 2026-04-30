# `get_project_health` Design

**Status:** Approved 2026-04-30

## Goal

Single MCP call that aggregates 7 health dimensions per project. Returns counts plus top-N hotspots per dimension inline so an agent can answer "how is this project doing?" without follow-up calls.

## API

```csharp
GetProjectHealthResult Execute(
    string? project = null,            // null = whole solution, grouped per project
    int hotspotsPerDimension = 5)      // top-N inline per dimension
```

## Output

```csharp
public record GetProjectHealthResult(
    IReadOnlyList<ProjectHealth> Projects);

public record ProjectHealth(
    string Project,
    ProjectHealthCounts Counts,
    ProjectHealthHotspots Hotspots);

public record ProjectHealthCounts(
    int ComplexityHotspots,
    int LargeClasses,
    int NamingViolations,
    int UnusedSymbols,
    int ReflectionUsages,
    int AsyncViolations,
    int DisposableMisuse);

public record ProjectHealthHotspots(
    IReadOnlyList<ComplexityMetric> Complexity,
    IReadOnlyList<LargeClassInfo> LargeClasses,
    IReadOnlyList<NamingViolation> Naming,
    IReadOnlyList<UnusedSymbolInfo> Unused,
    IReadOnlyList<ReflectionUsage> Reflection,
    IReadOnlyList<AsyncViolation> Async,
    IReadOnlyList<DisposableMisuseInfo> Disposable);
```

Hotspot lists reuse the existing models from each underlying tool — no new per-dimension shapes, just trimmed to top N per dimension. Sorted by severity proxy: highest cyclomatic complexity first, largest classes first, etc.

## Architecture

### Composition

1. Run each of the 7 underlying `*Logic.Execute` methods sequentially (each is itself parallel internally where it matters; composite-level parallelism would compete for the same `LoadedSolution`).
2. For each tool, pass through the `project` filter so it scopes its scan.
3. Group results by project name (each underlying entry has a `Project` field).
4. For each project, compute counts (full list lengths) and trim each list to `hotspotsPerDimension` after sorting by severity proxy.
5. Sort the final `Projects` list ascending by name for deterministic output.

### Underlying tools and their threshold/sort defaults

| Dimension | Logic class | Default threshold | Severity proxy (sort desc) |
|---|---|---|---|
| Complexity | `GetComplexityMetricsLogic` | `threshold: 10` (matches existing default) | `CyclomaticComplexity` |
| Large classes | `FindLargeClassesLogic` | `maxMembers: 20`, `maxLines: 500` | `LineCount` |
| Naming | `FindNamingViolationsLogic` | n/a | none — preserve underlying order |
| Unused | `FindUnusedSymbolsLogic` | `includeInternal: false` | none — preserve underlying order |
| Reflection | `FindReflectionUsageLogic` | n/a | none — preserve underlying order |
| Async | `FindAsyncViolationsLogic` | n/a | by `Severity` desc, then file/line |
| Disposable | `FindDisposableMisuseLogic` | n/a | by `Severity` desc, then file/line |

Thresholds use the existing defaults so the composite is a faithful aggregate of each tool's "default audit" mode.

### Test projects

Skip — consistent with the 7 underlying tools (they all skip via `TestProjectDetector`). Test projects skew the metrics (test methods deliberately small, naming differs, etc.).

### Project filter

When `project` is non-null:
- Only that project appears in the result (single-element list).
- Each underlying tool receives the same filter, so they only walk that project.

When null: every production project gets an entry, including projects with zero findings (zero-counts, empty hotspot lists).

## Edge cases

| Case | Handling |
|---|---|
| Project with zero findings in all dimensions | Entry present, all counts 0, all hotspot lists empty |
| `hotspotsPerDimension <= 0` | Treated as 0 (counts still populated; hotspot lists empty) |
| Unknown project name | Empty `Projects` list (no error — same as the underlying tools) |
| Project containing only generated code | Skipped if all symbols filter out (consistent with underlying tools) |

## Testing

Fixture: existing `TestLib` / `TestLib2` already exercise complexity, naming, large-class, unused, reflection, async, and disposable patterns. No new fixture file needed.

Tests:
- `Result_HasOneEntryPerProductionProject`
- `Counts_MatchUnderlyingToolOutput` — call e.g. `GetComplexityMetricsLogic.Execute(_, _, "TestLib", 10)` directly, assert count matches `Counts.ComplexityHotspots` for that project
- `Hotspots_TrimmedToRequestedSize`
- `ProjectFilter_OnlyReturnsRequestedProject`
- `Hotspots_Complexity_SortedByCyclomaticDesc`
- `Hotspots_LargeClasses_SortedByLineCountDesc`
- `EmptyHotspotsRequest_StillPopulatesCounts` — `hotspotsPerDimension: 0` → counts non-zero, hotspots empty
- `UnknownProject_ReturnsEmptyList`
- `TestProjectsAreSkipped` — RoslynCodeLens.Tests etc. don't appear in the result

## Performance

Benchmark added: `get_project_health: whole solution`. Expected to be approximately the sum of the 7 underlying benchmarks, since the composite runs them sequentially. Acceptable for an audit tool.

## MCP wrapper

Standard pattern matching `find_uncovered_symbols` (another whole-solution audit composite):

```csharp
[McpServerToolType]
public static class GetProjectHealthTool
{
    [McpServerTool(Name = "get_project_health")]
    [Description(...)]
    public static GetProjectHealthResult Execute(
        MultiSolutionManager manager,
        string? project = null,
        int hotspotsPerDimension = 5)
    { ... }
}
```

Auto-registered via `WithToolsFromAssembly()` — no `Program.cs` edit.

## Out of scope (deferred)

- Numeric "health score" or letter grade — agent can compute client-side from counts; opinionated weights would surprise users
- Trend over time — would require persistence layer
- Configurable dimension list — YAGNI; if the agent wants one dimension they call the underlying tool directly
- Cross-project rollup ("solution-wide grade") — agent can sum counts client-side

## File checklist

- `src/RoslynCodeLens/Tools/GetProjectHealthLogic.cs`
- `src/RoslynCodeLens/Tools/GetProjectHealthTool.cs`
- `src/RoslynCodeLens/Models/GetProjectHealthResult.cs`
- `src/RoslynCodeLens/Models/ProjectHealth.cs`
- `src/RoslynCodeLens/Models/ProjectHealthCounts.cs`
- `src/RoslynCodeLens/Models/ProjectHealthHotspots.cs`
- `tests/RoslynCodeLens.Tests/Tools/GetProjectHealthToolTests.cs`
- `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs` — add benchmark
- `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` — Red Flags, Quick Reference, Understanding-a-Codebase
- `README.md` — Features list
- `CLAUDE.md` — bump tool count
