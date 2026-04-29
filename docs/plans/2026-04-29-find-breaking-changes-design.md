# `find_breaking_changes` — Design

**Date:** 2026-04-29
**Backlog source:** `docs/BACKLOG.md` § 2 (API & breaking-change tools)
**Successor to:** `get_public_api_surface` (shipped in PR #132)

## Goal

Compare the current solution's public API surface against a baseline (either a JSON snapshot of a prior `get_public_api_surface` run, or a baseline `.dll` file) and classify the differences as breaking or non-breaking. Output is a deterministic flat list sorted by severity (Breaking first), then name ASC, plus per-kind/per-severity summary buckets.

This is the natural follow-up to `get_public_api_surface`: that tool produces the surface, this tool compares two of them.

## Why

Library authors cutting a release ask one question above all: "will this break my consumers?" Existing tooling answers it via `dotnet build` + `Microsoft.CodeAnalysis.PublicApiAnalyzers` (heavyweight, requires a committed `PublicAPI.txt`) or via Microsoft's `ApiCompat` (CLI tool, separate workflow). Neither is callable from an MCP agent in the middle of a code review.

This tool gives the agent a one-call answer:

> "Removed `OrderService.Submit(int)` is breaking. Added `OrderService.SubmitAsync(int)` is non-breaking. `Foo.Cancel()` accessibility narrowed to Protected — breaking."

---

## Tool surface

```
find_breaking_changes(baselinePath: string)
```

`baselinePath` is auto-detected by file extension:
- `.json` → deserialize as `GetPublicApiSurfaceResult` snapshot from a prior `get_public_api_surface` run
- `.dll` → load via `MetadataReference.CreateFromFile`, walk `IAssemblySymbol.GlobalNamespace`, extract entries

Internally both paths produce a `IReadOnlyList<PublicApiEntry>`; the comparator is shape-agnostic.

### Output

```jsonc
{
  "summary": {
    "totalChanges": 23,
    "byKind": {
      "Removed": 3,
      "Added": 14,
      "KindChanged": 1,
      "AccessibilityNarrowed": 1,
      "AccessibilityWidened": 4
    },
    "bySeverity": { "Breaking": 5, "NonBreaking": 18 }
  },
  "changes": [
    {
      "kind": "Removed",
      "severity": "Breaking",
      "name": "MyApp.OrderService.Submit(int)",
      "entityKind": "Method",
      "project": "MyApp.Core",
      "filePath": "OrderService.cs",
      "line": 42,
      "details": "Method 'MyApp.OrderService.Submit(int)' removed from MyApp.Core"
    },
    {
      "kind": "AccessibilityNarrowed",
      "severity": "Breaking",
      "name": "MyApp.OrderService.Cancel()",
      "entityKind": "Method",
      "project": "MyApp.Core",
      "filePath": "OrderService.cs",
      "line": 53,
      "details": "Accessibility narrowed: Public → Protected"
    },
    {
      "kind": "Added",
      "severity": "NonBreaking",
      "name": "MyApp.OrderService.SubmitAsync(int)",
      "entityKind": "Method",
      "project": "MyApp.Core",
      "filePath": "OrderService.cs",
      "line": 67,
      "details": "Method 'MyApp.OrderService.SubmitAsync(int)' added"
    }
  ]
}
```

Sort: `severity` ASC (`Breaking = 0` first), then `name` ordinal ASC. `Breaking` before `NonBreaking` because the user prioritises blockers. Within a severity, alphabetical by name yields stable diff-friendly output.

---

## Five change kinds

| Kind | Severity | Detected when |
|------|----------|--------------|
| `Removed` | Breaking | Name in baseline, not in current |
| `Added` | NonBreaking | Name in current, not in baseline |
| `KindChanged` | Breaking | Same name, different `Kind` (Class↔Struct, Class↔Interface, etc.) |
| `AccessibilityNarrowed` | Breaking | Same name, `Public → Protected` |
| `AccessibilityWidened` | NonBreaking | Same name, `Protected → Public` |

`Severity` is hardcoded per kind. Library-author-by-default — the tool is opinionated.

---

## Algorithm

Single-pass diff:

```
baseline := LoadBaseline(baselinePath)            // List<PublicApiEntry>
current  := GetPublicApiSurfaceLogic.Execute(...) // List<PublicApiEntry>

baselineByName := baseline.ToDictionary(e => e.Name)
currentByName  := current.ToDictionary(e => e.Name)

for each baseline entry b:
    if b.Name not in currentByName:
        emit Removed (Breaking) with b's location
    else:
        c := currentByName[b.Name]
        if b.Kind != c.Kind:
            emit KindChanged (Breaking) with c's location
        else if b.Accessibility == Public and c.Accessibility == Protected:
            emit AccessibilityNarrowed (Breaking) with c's location
        else if b.Accessibility == Protected and c.Accessibility == Public:
            emit AccessibilityWidened (NonBreaking) with c's location

for each current entry c whose name not in baselineByName:
    emit Added (NonBreaking) with c's location

sort by severity ASC, then name ordinal ASC
build summary
return result
```

Linear time, both in baseline size and current size. No quadratic scans.

### Baseline loading

**JSON path**:
```csharp
var json = File.ReadAllText(baselinePath);
var result = JsonSerializer.Deserialize<GetPublicApiSurfaceResult>(json, jsonOptions);
return result?.Entries ?? [];
```

The JSON shape is exactly what `get_public_api_surface` emits. `JsonSerializer` with `JsonStringEnumConverter` for `PublicApiKind`/`PublicApiAccessibility` enums.

**Assembly path**:
```csharp
var reference = MetadataReference.CreateFromFile(dllPath);
var compilation = CSharpCompilation.Create("baseline-extraction", references: [reference]);
var assemblySymbol = (IAssemblySymbol?)compilation.GetAssemblyOrModuleSymbol(reference);
var entries = ExtractEntriesFromAssembly(assemblySymbol, projectName: <derived from DLL name>);
return entries;
```

`ExtractEntriesFromAssembly` walks `assemblySymbol.GlobalNamespace` using the same recursion as `GetPublicApiSurfaceLogic.EnumerateTypes`/`EnumerateNestedTypes` (which we will extract into a shared helper — see Reuse section). It produces `PublicApiEntry`s but with file/line typically blank or set to the DLL path (since we don't have source for metadata). The `IsImplicitlyDeclared` filter still applies.

The "project name" for assembly entries is derived from the DLL filename (e.g., `MyApp.Core.dll` → `MyApp.Core`).

---

## Reuse + small refactor

| Component | Source | Action |
|-----------|--------|--------|
| `GetPublicApiSurfaceLogic.Execute` | shipped | Call from this tool to get the current surface |
| `EnumerateTypes` / `EnumerateNestedTypes` | private in `GetPublicApiSurfaceLogic` | **Extract** to `Analysis/PublicApiSurfaceExtractor.cs`. Both source-walk and assembly-walk paths need this. Pure recursion over `INamespaceSymbol` / `INamedTypeSymbol`, no Roslyn-version-specific code. |
| `IsApiVisibleType`, `ClassifyMemberAccessibility`, `BuildTypeEntry`, `BuildMemberEntry`, `MemberKindAndName`, `TypeKindToApiKind`, `ApiMemberFormat`, `MemberDisplayName`, `FullyQualified` | private in `GetPublicApiSurfaceLogic` | **Extract** to the same `PublicApiSurfaceExtractor`. The assembly path needs all of them. The source-walk path in `GetPublicApiSurfaceLogic` becomes a thin wrapper. |

This is the right time for the extraction — `GetPublicApiSurfaceLogic` was scoped to "do it once", and now we need it twice. The shared extractor is a single class with two public entry points:

```csharp
public static class PublicApiSurfaceExtractor
{
    // Source walk — used by GetPublicApiSurfaceLogic
    public static IReadOnlyList<PublicApiEntry> ExtractFromSource(LoadedSolution loaded, SymbolResolver source, ImmutableHashSet<ProjectId> testProjectIds);

    // Assembly walk — used by FindBreakingChangesLogic
    public static IReadOnlyList<PublicApiEntry> ExtractFromAssembly(IAssemblySymbol assembly, string projectName);
}
```

Behavior preserved for `get_public_api_surface` — same tests still pass.

---

## Edge cases

| Case | Behaviour |
|------|-----------|
| Baseline file doesn't exist | Throw `FileNotFoundException` with a clear message; the MCP tool's framework surfaces it to the caller. |
| Baseline JSON is malformed | Throw a descriptive `InvalidOperationException` ("Baseline JSON is not a valid get_public_api_surface result: \<details\>"). |
| Baseline assembly fails to load | Throw `InvalidOperationException` wrapping the underlying Roslyn exception. |
| Both baseline and current empty | Return zero changes, summary all-zero. No error. |
| Same name appears multiple times in baseline (corrupt input) | Use the **last** occurrence (default `ToDictionary` behavior); document as caller's responsibility. |
| `Internal → Public` in current (not in baseline at all because internal isn't part of API) | Reports as `Added`. Correct. |
| `Public → Internal` in current (disappears from API) | Reports as `Removed`. Correct. |
| Generic-type-parameter rename (`Container<T>` → `Container<TKey>`) | The FQN includes type parameters, so this looks like Removed + Added. Documented limitation; consumers can mentally pair them. |
| Method overload added (existing name kept) | Old name unchanged; new name `Added`. No false `Removed` because the overload set is identified by full signature. |
| Accessibility transition that's both narrowed and widened (e.g., `Public → Protected → Internal`) | We compare baseline directly to current — only the net delta matters. `Public → Internal` would currently be reported as `Removed` (since Internal isn't in the API surface). Correct. |
| Member moved between types (e.g., `A.M()` deleted, `B.M()` added) | Two events: `Removed` + `Added`. We don't disambiguate moves. |

---

## Hardcoded constants

| Constant | Value | Why hardcoded |
|----------|-------|---------------|
| Severity per kind | per the table above | Library-author convention; agents can post-filter if they disagree |
| Sort | severity ASC, then name ASC ordinal | Deterministic, useful for diff |

No tool parameters beyond `baselinePath`. No env vars.

---

## Tests

### Fixtures

This tool needs **JSON baseline fixtures** (small files representing different API states) plus a way to test the assembly-loading path.

#### JSON baseline fixtures

Add `tests/RoslynCodeLens.Tests/Fixtures/Baselines/`:
- `baseline-no-changes.json` — exactly matches the current `TestLib` surface (no diffs expected)
- `baseline-with-removals.json` — includes a fake symbol that doesn't exist in current → produces `Removed`
- `baseline-with-kind-change.json` — has `TestLib.Greeter` listed as `Struct` instead of `Class` → produces `KindChanged`
- `baseline-with-accessibility-narrowed.json` — has a member listed as `Public` that's `Protected` in current → `AccessibilityNarrowed`
- `baseline-with-accessibility-widened.json` — `Protected` baseline, `Public` current → `AccessibilityWidened`
- `baseline-with-removed-and-added.json` — covers a "rename" case: removes a name, expects current to have a different name added

#### Assembly path test

Use one of the existing fixture project's compiled DLLs (e.g., `TestLib2.dll` from a Debug build) as a baseline assembly. Assert the diff matches expectations: since `TestLib2.dll` IS part of the current solution too, the diff should be effectively zero (both surfaces match).

To stress the assembly path, we can compile a tiny synthetic assembly fixture (one type, two methods) using `CSharpCompilation` + `EmitToStream` in test setup — emit to a temp file, point the tool at it. This avoids depending on build-time artifacts.

### Test cases

`tests/RoslynCodeLens.Tests/Tools/FindBreakingChangesToolTests.cs`:

| Test | Asserts |
|------|---------|
| `Json_NoChanges_ReturnsEmpty` | matching baseline produces 0 changes |
| `Json_RemovedSymbol_ReportedAsRemovedBreaking` | name in baseline, not in current → `Removed` + `Breaking` |
| `Json_AddedSymbol_ReportedAsAddedNonBreaking` | name in current, not in baseline → `Added` + `NonBreaking` |
| `Json_KindChange_ReportedAsKindChangedBreaking` | `Class → Struct` produces `KindChanged` + `Breaking` |
| `Json_AccessibilityNarrowed_Breaking` | `Public → Protected` produces `AccessibilityNarrowed` + `Breaking` |
| `Json_AccessibilityWidened_NonBreaking` | `Protected → Public` produces `AccessibilityWidened` + `NonBreaking` |
| `Assembly_BaselineDll_RoundtripsCleanly` | compile a tiny in-memory fixture, save to temp DLL, point the tool at it. Assert the diff against the same source produces 0 changes. |
| `Severity_BreakingBeforeNonBreaking_InSort` | sorted output's leading items are all `Breaking`, then all `NonBreaking` |
| `Within_Severity_NameAscending` | within each severity bucket, names are ordinal-ASC |
| `Summary_ByKindCountsAreCorrect` | aggregate consistency |
| `Summary_BySeverityCountsAreCorrect` | aggregate consistency |
| `Summary_TotalMatchesListLength` | sanity |
| `MissingBaselineFile_ThrowsFileNotFound` | error path |
| `MalformedBaselineJson_ThrowsInvalidOperation` | error path |

Also: a regression test that the existing `GetPublicApiSurfaceToolTests` all still pass after the `PublicApiSurfaceExtractor` extraction (shouldn't, but verifies the refactor is behavior-preserving).

### Benchmark

```csharp
[Benchmark(Description = "find_breaking_changes: JSON baseline (matching current)")]
public object FindBreakingChangesJson()
    => FindBreakingChangesLogic.Execute(_loaded, _resolver, _baselineJsonPath);
```

Use a pre-generated baseline JSON in benchmarks fixtures. Skip the assembly path in benchmarks for now (involves temp-file IO; not the hot path).

---

## SKILL.md / README updates

**SKILL.md** — Red Flags routing row:
> | "Will this break consumers?" / "Show me breaking changes vs the previous release" / "Diff this build's API against baseline" | `find_breaking_changes` |

Add to the API/Inspection section:
> - `find_breaking_changes` — Diff the current public API surface against a baseline (JSON snapshot from `get_public_api_surface`, or a `.dll` file). Reports Removed/Added/KindChanged/AccessibilityNarrowed/Widened with Breaking/NonBreaking severity.

**README** — Features list near `get_public_api_surface`:
> - **find_breaking_changes** — Diff the current API against a baseline JSON or DLL; report removed members, kind changes, and accessibility changes with breaking/non-breaking severity.

**CLAUDE.md** — tool count 27 → 28.

---

## Out of scope (explicit non-goals)

- **Return-type changes** — the FQN doesn't include return type. Documented limitation; would require extending `PublicApiEntry` schema.
- **Sealed-ness changes** — `class Foo` → `sealed class Foo`. Same constraint.
- **Abstract member additions** to non-sealed class. Same constraint.
- **Nullable annotation changes** (`string?` ↔ `string`). Same constraint.
- **Parameter name changes** (which only affect named-arg call sites). The FQN uses parameter types, not names.
- **NuGet package as baseline** — no network/cache dependency in v1; assembly path covers `~/.nuget/packages/...` if user points at a cached DLL.
- **Project / namespace filter** — whole-solution; add only on demand.
- **Fix suggestions** — analysis only.
- **No tool parameters** beyond `baselinePath`. No env vars.

---

## Decisions log

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Baseline sources | JSON + DLL | Real-world workflow needs DLL; JSON is essentially free |
| Change kinds | Standard 5 (Removed/Added/KindChanged/Narrowed/Widened) | Free given current `PublicApiEntry` schema; covers ~85% of real breaks |
| Severity assignment | Hardcoded per kind | Library-author convention; no team disagreement |
| Output shape | Summary + flat list | Established convention across all our analysis tools |
| Sort | severity ASC, name ASC | Breaking first; deterministic |
| Identity matching | Full FQN incl. parameter types | Already determined by `get_public_api_surface`; overloads correctly distinguished |
| Refactor for shared extractor | Extract `PublicApiSurfaceExtractor` | Both source-walk and assembly-walk paths need it |
| Baseline assembly loading | `MetadataReference.CreateFromFile` + throwaway compilation | Roslyn-native, no extra deps |
| Filters / parameters | Just `baselinePath` | Project's convention |
