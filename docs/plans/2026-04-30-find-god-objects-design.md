# `find_god_objects` Design

**Status:** Approved 2026-04-30

## Goal

Identify types that combine high size with high coupling — "god classes" that violate SRP and become refactoring nightmares. Sharper signal than `find_large_classes` size alone: a 1000-line internal helper used only by its own namespace is not a god object, but a 200-line class called from 15 different namespaces is.

## Use cases

- "What's the worst design smell in this codebase?" — list the top god objects across the solution.
- "Before I refactor, where should I start?" — prioritise types whose change would ripple widely.
- Pairs with `get_project_health` as a higher-quality signal than raw size.

## Definition

A type qualifies as a god object when it crosses **both** axes:

**Size axis** (ALL THREE must be exceeded — sharper heuristic; a DTO that exceeds only the field count, or a dispatcher that exceeds only the member count, is not a god object):
- `>=300` lines
- `>=15` members
- `>=10` fields

**Coupling axis** (any one):
- `≥5` incoming-namespace callers (distinct namespaces that reference this type)
- `≥5` outgoing-namespace dependencies (distinct non-BCL namespaces this type references)

**Why namespaces, not raw counts**: a type with 50 callers in its own namespace is internally cohesive. 50 callers across 20 namespaces is a coupling sink. Namespace count is the cleaner signal.

## API

```csharp
GodObjectsResult Execute(
    string? project = null,
    int minLines = 300,
    int minMembers = 15,
    int minFields = 10,
    int minIncomingNamespaces = 5,
    int minOutgoingNamespaces = 5)
```

## Output

```csharp
public record GodObjectsResult(
    IReadOnlyList<GodObjectInfo> Types);

public record GodObjectInfo(
    string TypeName,
    int LineCount,
    int MemberCount,
    int FieldCount,
    int IncomingNamespaces,
    int OutgoingNamespaces,
    IReadOnlyList<string> SampleIncoming,    // 5 namespace names that depend on this, alphabetical
    IReadOnlyList<string> SampleOutgoing,    // 5 namespace names this depends on, alphabetical
    string FilePath,
    int Line,
    string Project,
    int SizeAxesExceeded,                    // 0..3
    int CouplingAxesExceeded);               // 0..2
```

Sort: `(SizeAxesExceeded + CouplingAxesExceeded) DESC, then LineCount DESC`. Worst offenders first.

## Architecture

### 1. Walk candidate types

Same enumeration as `FindLargeClassesLogic`:
- Production projects only (skip test projects via `TestProjectDetector`).
- Classes, structs, records — interfaces excluded (size of an interface declaration doesn't make it god-like).
- Skip generated code (`GeneratedCodeDetector`).
- Skip nested types (rarely god objects in their own right).

For each candidate, compute size axes from the syntax tree:
- `LineCount` = end-line minus start-line of the type declaration.
- `MemberCount` = `IMethodSymbol` + `IPropertySymbol` + `IFieldSymbol` + `IEventSymbol`.
- `FieldCount` = `IFieldSymbol` only.

### 2. Filter to size-suspects

Compute `SizeAxesExceeded`. Drop types where `SizeAxesExceeded == 0` — they have no business being on the list regardless of coupling. This narrows the candidate set drastically (typically 5-20 per project from thousands), making coupling analysis affordable.

### 3. Compute incoming-namespace coupling

For each suspect type, walk the solution's syntax trees once and collect: every node that resolves to a member of this type, take the *caller's* containing namespace. Distinct count → `IncomingNamespaces`.

Implementation: a single pass over all syntax trees per candidate (or batched — a dictionary keyed by symbol with namespace-set values, populated in one combined walk). Self-namespace excluded.

### 4. Compute outgoing-namespace coupling

Walk the candidate type's own syntax: every `InvocationExpressionSyntax`, `ObjectCreationExpressionSyntax`, `MemberAccessExpressionSyntax` resolved to a member's containing namespace. Distinct count → `OutgoingNamespaces`. Excludes:
- Self-namespace (a type using its own namespace's helpers is fine).
- BCL namespaces (`System.*`, `Microsoft.*`) — every type uses `string`/`int`/`Task`; this would dilute the signal.

### 5. Filter and sort

Keep types where `SizeAxesExceeded ≥ 1 AND CouplingAxesExceeded ≥ 1`. Sort by total axes exceeded descending, then `LineCount` descending. Truncate `SampleIncoming` / `SampleOutgoing` to 5 each, sorted ASC for stability.

## Edge cases

| Case | Handling |
|---|---|
| Type referenced via reflection only | Not detected — incoming-namespace count won't reflect it. Acceptable; reflection coupling is a separate problem (`find_reflection_usage`). |
| `[ApiController]` types with many endpoints by design | Will likely flag — agent can pass higher `minMembers` to suppress. |
| Generic type instantiations | Each `ContainingType.OriginalDefinition` resolved once; instantiations counted as the same type. |
| Cross-compilation references | Counted normally — the namespace of the *referencing* symbol is what matters. |
| Two types in the same file | Each evaluated independently. |

## Performance

The expensive step (incoming-namespace count) only runs on size-suspects. Estimated ~10-50 candidates per typical project. For each candidate, one solution-wide syntax walk. Total: O(candidates × syntax-trees), comparable to a single `FindCallersLogic.Execute` per candidate.

Benchmark planned: `find_god_objects: whole solution`.

## Testing

Fixture: add `GodObjectSamples.cs` to `TestLib/` with:
- `BadGodObject` — large class (≥300 lines synthetic) referenced from multiple synthetic namespaces.
- `LargeButIsolated` — large class only referenced by its own namespace.
- `SmallButHighlyCoupled` — small class referenced from many namespaces.

Tests:
- `Result_FindsKnownGodObject`
- `Result_DoesNotFlag_LargeButIsolated` (size met, coupling not → not flagged)
- `Result_DoesNotFlag_SmallButHighlyCoupled` (coupling met, size not → not flagged)
- `Result_SortedByAxesExceededDesc`
- `IncomingNamespaces_ExcludesOwnNamespace`
- `OutgoingNamespaces_ExcludesOwnNamespace`
- `OutgoingNamespaces_ExcludesBclTypes`
- `ProjectFilter_OnlyReturnsRequestedProject`
- `Thresholds_AreConfigurable`
- `SampleIncoming_LimitedToFive`
- `EmptyResult_NoExceptionWhenNoneQualify`
- `Interfaces_AreNotFlagged`
- `NestedTypes_AreNotFlagged`

## Out of scope (deferred)

- ML-based detection — heuristic is enough.
- Splitting/refactoring suggestions — caller's judgment.
- Reflection-coupling counted toward incoming-namespace tally — separate concern.

## File checklist

- `src/RoslynCodeLens/Tools/FindGodObjectsLogic.cs`
- `src/RoslynCodeLens/Tools/FindGodObjectsTool.cs`
- `src/RoslynCodeLens/Models/GodObjectsResult.cs`
- `src/RoslynCodeLens/Models/GodObjectInfo.cs`
- `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/GodObjectSamples.cs`
- `tests/RoslynCodeLens.Tests/Tools/FindGodObjectsToolTests.cs`
- `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs` — add benchmark
- `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` — Red Flags, Quick Reference, Code Quality
- `README.md` — Features list
- `CLAUDE.md` — bump tool count
