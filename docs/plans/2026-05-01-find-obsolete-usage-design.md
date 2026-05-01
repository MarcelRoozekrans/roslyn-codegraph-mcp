# `find_obsolete_usage` Design

**Status:** Approved 2026-05-01

## Goal

Find every call site / reference to `[Obsolete]`-marked symbols, grouped by deprecation message and severity. Sharper than generic `find_attribute_usages` for the "plan a deprecation migration" workflow.

## Use cases

- "What deprecations do we still use?" — list grouped by message
- "Show me must-fix obsoletions" — `errorOnly: true` filters to `[Obsolete(..., true)]` markers
- "Which deprecated NuGet APIs are we calling?" — metadata-marked obsoletes are included

## Why grouped, not a flat list

A flat list of 200 individual call sites doesn't help with planning. Grouping by `(symbol, deprecation message)` tells the agent "we have 5 distinct deprecations pending; this one has 80 sites and is an error; that one has 3 sites and is a warning." The agent can prioritise per group.

## API

```csharp
FindObsoleteUsageResult Execute(
    string? project = null,
    bool errorOnly = false)
```

- `project` — restrict scan (whole-solution if null)
- `errorOnly` — skip warning-level deprecations (focus on must-fix)

## Output

```csharp
public record FindObsoleteUsageResult(
    IReadOnlyList<ObsoleteSymbolGroup> Groups);

public record ObsoleteSymbolGroup(
    string SymbolName,
    string DeprecationMessage,         // empty string if attribute has no message
    bool IsError,
    int UsageCount,
    IReadOnlyList<ObsoleteUsageSite> Usages);

public record ObsoleteUsageSite(
    string CallerName,
    string FilePath,
    int Line,
    string Snippet,
    string Project,
    bool IsGenerated);
```

Sort: groups by `IsError DESC, UsageCount DESC, SymbolName ASC` (errors-with-most-usage first). Usages within a group by `(FilePath, Line)`.

## Architecture

### 1. Find obsolete symbols

Walk every `IMethodSymbol` / `IPropertySymbol` / `IEventSymbol` / `INamedTypeSymbol` (and `IFieldSymbol`) reachable from the loaded compilations. Check each for an attribute whose `AttributeClass.ToDisplayString() == "System.ObsoleteAttribute"`. Capture:
- `Message` — first constructor argument (string), default `""`
- `IsError` — second constructor argument (bool), default `false`

Includes metadata-resolved symbols. The common real-world case is "third-party NuGet API deprecated; find every call site we have."

### 2. Find usages per obsolete symbol

Same matching approach as `FindCallersLogic`:
- Walk syntax trees of all production projects
- For each `InvocationExpressionSyntax`, `ObjectCreationExpressionSyntax`, `MemberAccessExpressionSyntax`, `IdentifierNameSyntax`: resolve via `SemanticModel.GetSymbolInfo`, compare to the obsolete-symbol set (with cross-compilation metadata fallback)
- For type-level deprecation: any reference to the type counts (object creation, type names in declarations, etc.)
- Dedup by `(file, span)` so two references on one line both register

### 3. Build groups

One `ObsoleteSymbolGroup` per obsolete symbol with ≥1 usage. Symbols with 0 usages omitted (no migration needed). `errorOnly: true` filters out groups with `IsError == false`.

### 4. Sort and return

Per the sort rules above.

## Scope decisions

| Concern | Decision |
|---|---|
| Test projects | Excluded as obsolete-symbol sources AND as usage sites (consistent with other audit tools) |
| Generated code | Usage sites included with `IsGenerated: true` flag (agent decides) |
| Metadata-marked obsoletes (NuGet) | **Included** — primary use case is third-party deprecation tracking |
| Whole types `[Obsolete]` | Counted at type level + each member access counts as a usage |
| `[ObsoleteAttribute]` vs `[Obsolete]` | Same — both resolve to `System.ObsoleteAttribute` |
| `DiagnosticId` / `UrlFormat` properties | Not currently surfaced — can be added later if agents need them |

## Edge cases

| Case | Handling |
|---|---|
| Obsolete attribute with no arguments | `Message: ""`, `IsError: false` |
| Obsolete on interface method, called via implementation | Both interface and implementation method are matched if both have `[Obsolete]`; otherwise only the marked one |
| Inherited deprecation (override doesn't repeat `[Obsolete]`) | Counted only at the declared site — Roslyn doesn't propagate the attribute |
| Obsolete on type used in `nameof(MyObsolete)` | Counted (the syntax walks `IdentifierNameSyntax`) |

## Testing

Fixture additions to `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/`:

- `ObsoleteSamples.cs` with:
  - `ObsoleteWarning` method (`[Obsolete("Use NewWay")]`) — called from 2 sites
  - `ObsoleteError` method (`[Obsolete("Hard fail", true)]`) — called from 1 site
  - `ObsoleteWithoutMessage` method (`[Obsolete]`) — called from 1 site
  - `ObsoleteType` class (`[Obsolete("Drop this")]`) — referenced via `new ObsoleteType()` and `nameof(ObsoleteType)`
  - A consumer class with the call sites

Tests:
- `Result_FindsObsoleteWithMessage`
- `Result_FindsObsoleteWithoutMessage`
- `Result_FindsObsoleteType`
- `Result_GroupsBySymbol`
- `UsageCount_MatchesSiteCount`
- `Sort_ErrorsBeforeWarnings`
- `Sort_WithinSameSeverity_HighestUsageFirst`
- `ErrorOnlyFilter_ExcludesWarnings`
- `ProjectFilter_OnlyReturnsRequestedProject`
- `Result_OmitsObsoleteWithZeroUsages` (define an unused obsolete in the fixture)
- `IsGenerated_FlagPropagatesToUsage`
- `MetadataObsolete_IsFound` (e.g. one of TestLib's calls into a known-deprecated BCL API like `System.Configuration.ConfigurationManager` if available, or skip if no clean fixture target exists)

## Performance

Single solution-wide syntax walk — comparable to `find_attribute_usages`. Obsolete-symbol enumeration is one pass over all types' members.

Benchmark: `find_obsolete_usage: whole solution`.

## MCP wrapper

Standard pattern matching `find_attribute_usages` — `[McpServerToolType]` static class, single `Execute` method via `MultiSolutionManager`.

## Out of scope (deferred)

These deferred items will also be appended to `docs/BACKLOG.md` under "Deferred from shipped features":

- **Reachability analysis** — whether each call site is reachable from a test or public entry point. `analyze_change_impact` already covers this; agent can compose.
- **Auto-migration suggestions** — agent's call.
- **`DiagnosticId` / `UrlFormat` properties** — surface them on the group record if agents start asking for them.
- **Inherited deprecation propagation** — Roslyn doesn't propagate `[Obsolete]` to overrides; could be inferred but agent can compose with `find_implementations`.

## File checklist

- `src/RoslynCodeLens/Tools/FindObsoleteUsageLogic.cs`
- `src/RoslynCodeLens/Tools/FindObsoleteUsageTool.cs`
- `src/RoslynCodeLens/Models/FindObsoleteUsageResult.cs`
- `src/RoslynCodeLens/Models/ObsoleteSymbolGroup.cs`
- `src/RoslynCodeLens/Models/ObsoleteUsageSite.cs`
- `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/ObsoleteSamples.cs`
- `tests/RoslynCodeLens.Tests/Tools/FindObsoleteUsageToolTests.cs`
- `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs` — add benchmark
- `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` — Red Flags, Quick Reference, Code Quality
- `README.md` — Features list
- `CLAUDE.md` — bump tool count
- `docs/BACKLOG.md` — append "Deferred from shipped features" section with this feature's deferred items
