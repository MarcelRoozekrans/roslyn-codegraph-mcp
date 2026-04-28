# `find_uncovered_symbols` — Design

**Date:** 2026-04-28
**Backlog source:** `docs/BACKLOG.md` § 1 (test-aware tools)
**Successor to:** `find_tests_for_symbol` (shipped 2026-04-26, PR #116)

## Goal

Surface untested public API as a prioritized testing-debt report. For every public method and property in production projects, determine whether any test method transitively reaches it (within bounded depth). Return uncovered symbols sorted by cyclomatic complexity descending, plus a coverage summary.

## Why

Agents asking "what should I write tests for?" or "where is testing debt highest?" have no first-class answer today. They can ask `find_tests_for_symbol` per candidate, but the inverted question (give me everything untested) requires hand-rolling. Folding complexity into the report turns "what's uncovered?" into "what's risky AND uncovered?" — a directly actionable list rather than a pile of low-value getters.

This is reference-based coverage — static analysis only, no runtime instrumentation. Real execution coverage from coverlet/dotCover is a separate tool for a separate day.

---

## Tool surface

```
find_uncovered_symbols()
```

No parameters in v1. Whole-solution scope, hardcoded thresholds.

### Output

```jsonc
{
  "summary": {
    "totalSymbols": 142,
    "coveredCount": 98,
    "uncoveredCount": 44,
    "coveragePercent": 69,
    "riskHotspotCount": 7
  },
  "uncoveredSymbols": [
    {
      "symbol": "OrderService.CalculateTotal",
      "kind": "method",            // method | property
      "location": "OrderService.cs:42",
      "project": "MyApp.Core",
      "complexity": 8
    },
    ...
  ]
}
```

`uncoveredSymbols` is sorted by `complexity DESC, symbol ASC`. Highest-risk untested code first.

`riskHotspotCount` counts uncovered symbols with `complexity >= 5` (McCabe moderate-risk boundary).

`coveragePercent` is `floor(coveredCount / totalSymbols * 100)`. If `totalSymbols == 0`, returns 100.

---

## Algorithm — inverted single forward sweep

The naive approach (per-symbol BFS upward) is O(N × BFS) — too slow on large solutions. We invert the direction: walk callees DOWN from each test once, accumulate the covered set, then diff.

### Steps

1. **Test method enumeration**
   - `TestProjectDetector.GetTestProjectIds(solution)` → set of test project IDs
   - For each test project's compilation, enumerate methods declared in source, classified as test by `TestMethodClassifier` (extracted from `FindTestsForSymbolLogic.ClassifyAsTest`)
   - Yields `IReadOnlyList<IMethodSymbol> testMethods`

2. **Downward callee walk → covered set**
   - Initialize `coveredSet = HashSet<IMethodSymbol>(SymbolEqualityComparer.Default)` and `visited = same`
   - BFS queue seeded with every test method at depth 0
   - For each frontier method:
     - Find every `InvocationExpressionSyntax` and `MemberAccessExpressionSyntax` (for property accesses) in its body
     - Resolve to `ISymbol` via `SemanticModel.GetSymbolInfo`
     - For methods: add to `coveredSet`; enqueue if `depth + 1 < maxDepth` and not already visited
     - For properties: add property's getter/setter `IMethodSymbol`s to `coveredSet`; enqueue accessor bodies the same way
   - `maxDepth = 3` hardcoded
   - Visited set keyed on `IMethodSymbol` prevents cycles

3. **Candidate enumeration**
   - For each non-test project's compilation:
     - Walk all `INamedTypeSymbol`s in the assembly
     - For each accessible type (public or internal — both can be testable):
       - Enumerate `IMethodSymbol` members where:
         - `DeclaredAccessibility == Public`
         - `Locations.Any(l => l.IsInSource)` (source-defined; skips inherited/abstract decls)
         - `MethodKind == Ordinary` (skips constructors, finalizers, operators, accessors)
         - Not `IsAbstract` (no body to cover)
       - Enumerate `IPropertySymbol` members where:
         - `DeclaredAccessibility == Public`
         - `Locations.Any(l => l.IsInSource)`
         - Has at least one accessor
   - Yields `IReadOnlyList<ISymbol> candidates`

4. **Diff**
   - For each candidate:
     - If method: `covered = coveredSet.Contains(method)`
     - If property: `covered = coveredSet.Contains(getter) || coveredSet.Contains(setter)` (any accessor invoked counts)
   - `uncovered = candidates.Where(!covered)`

5. **Complexity attribution**
   - For each uncovered symbol, compute cyclomatic complexity using the same engine `GetComplexityMetricsLogic` already uses
   - Properties: complexity is the max of getter/setter complexities (auto-property → 1)

6. **Output assembly**
   - Build summary stats
   - Sort uncovered by `complexity DESC, fullyQualifiedName ASC`
   - Emit `FindUncoveredSymbolsResult`

### Performance characteristics

- Cost: O(test_methods × avg_callees × maxDepth) + O(candidates) + O(uncovered × complexity_per_symbol)
- Typical 5-project solution with 200 public symbols and 50 tests: under 1 second
- Large 50-project solution with 2000 public symbols and 500 tests: under 10 seconds (estimated)
- Visited-set ensures we never walk the same method twice across all tests

---

## Hardcoded constants

| Constant | Value | Why hardcoded |
|----------|------:|---------------|
| `maxDepth` | 3 | Matches `find_tests_for_symbol`; covers ≥99% of real test paths (1–2 helper hops); risk of false-positive uncovered is documented |
| `riskThreshold` | 5 | McCabe moderate-risk boundary; industry standard; agents can post-filter on `complexity` field if they disagree |

No tool parameters, no env vars, no config file. Same justification as the rest of this codebase: the project has zero env-var-driven config today, and tunables here would be speculative. Adding parameters later (if real demand emerges) is a five-minute change.

---

## Reuse + refactor

| Component | Source | Action |
|-----------|--------|--------|
| `TestProjectDetector` | shipped in `find_tests_for_symbol` | Reuse as-is |
| `TestAttributeRecognizer` | shipped in `find_tests_for_symbol` | Reuse as-is |
| `ClassifyAsTest` (private in `FindTestsForSymbolLogic`) | duplicated here would be smelly | **Refactor** — extract into `TestDiscovery/TestMethodClassifier.cs`, update both call sites |
| Cyclomatic complexity | `GetComplexityMetricsLogic` | Reuse the existing complexity calculator (will identify exact API when writing the implementation plan) |

The `TestMethodClassifier` extraction is small (move ~20 lines of method, update one caller, add the new caller). It's worth doing now to prevent drift between the two tools.

---

## Edge cases

| Case | Behaviour |
|------|-----------|
| No test projects in solution | Return `summary` with `totalSymbols`, `coveredCount=0`, `uncoveredCount=totalSymbols`, `coveragePercent=0`. All public symbols are uncovered. |
| No public symbols in production projects | Return `summary` with all zeros and `coveragePercent=100`. Empty `uncoveredSymbols`. |
| Test method calls a method via reflection | Not detected (we follow syntactic invocations only). Symbol appears as uncovered. Documented limitation. |
| Test path > 3 hops | Symbol appears as uncovered. Documented limitation. |
| Public method only called from another public method that IS tested | Covered (the inner method ends up in `coveredSet` via the BFS). |
| Auto-property | Complexity 1. Counted in totals. Marked uncovered if neither accessor is reached. |
| Abstract method declaration | Skipped from candidates (no body to test). |
| Interface method declaration | Skipped from candidates (no body). |
| Generic methods | Treated by `OriginalDefinition` so all closed instantiations collapse to one candidate. |
| Compiler-generated methods (e.g. iterators, async state machines) | Skipped via `IsImplicitlyDeclared` filter. |

---

## Tests

### Fixture additions

The existing fixture (`tests/RoslynCodeLens.Tests/Fixtures/TestSolution/`) already has the right shape:
- `TestLib` and `TestLib2` are production projects with public methods/properties
- `XUnitFixture`, `NUnitFixture`, `MSTestFixture` are test projects calling production code
- `Greeter.GreetFormal` is already a public method that no test calls — perfect uncovered candidate

We add a small handful more to `TestLib` so the test suite has variety:
- A computed property with logic (complexity > 1)
- A public method called only via a helper (transitive coverage)
- A public method called only via a helper at depth 4 (exceeds maxDepth — false-positive uncovered case)
- A high-complexity uncovered method (complexity ≥ 5) for the riskHotspotCount test

### Test cases

| File | What it covers |
|------|---------------|
| `Tools/FindUncoveredSymbolsToolTests.cs` | Direct uncovered method present, covered method absent, summary counts add up, sort order (complexity DESC), riskHotspotCount honors threshold, transitive coverage via helper, depth-exceeded false-positive, auto-property handled, computed property complexity > 1, kind field correct (method vs property) |
| `TestDiscovery/TestMethodClassifierTests.cs` | New test for the extracted classifier (parity with old behaviour from `FindTestsForSymbolToolTests`) |

### Benchmark

Add to `CodeGraphBenchmarks.cs`:

```csharp
[Benchmark(Description = "find_uncovered_symbols: whole solution")]
public object FindUncoveredSymbols()
    => FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);
```

---

## SKILL.md update

Add to the routing table:

> | "What should I write tests for?" / "Where's our testing debt?" / "Show me untested public methods" | `find_uncovered_symbols` |

Add to the "Finding Dependencies and Usage" section. Add to the metadata-support table as "N/A" (only operates on source).

## README

Add to Features list near `find_tests_for_symbol`:

> - **find_uncovered_symbols** — Public methods and properties no test transitively reaches; sorted by cyclomatic complexity for prioritization

Update tool count: 23 → 24 (CLAUDE.md too).

---

## Out of scope (explicit non-goals)

- No execution coverage from coverlet/dotCover XML — different feature, runtime data.
- No `project` / `namespace` filter — whole-solution; add filter only if demand emerges.
- No tunable `maxDepth` or `riskThreshold` — hardcoded; add parameters only if demand emerges.
- No fields, events, indexers, constructors, finalizers — not the testable behavior surface.
- No abstract methods or interface declarations — no body to test.
- No reflection-mediated coverage detection — syntactic only.
- No "tests touch this symbol but never assert anything" detection — out of scope.

---

## Decisions log

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Symbol scope | Public methods + properties | Methods + properties are the testable behavior surface; fields are mostly data |
| Coverage definition | Transitively reachable from a test method | Direct-only is too strict (misses helper-mediated tests); any-ref-from-test-project is too weak (false positives) |
| Output shape | Coverage report (summary + per-symbol) | Same engine cost as binary list; richer answer to the agent's actual question |
| Include complexity | Yes | Differentiates trivial getters from real testing debt; reuses existing engine |
| Sort order | complexity DESC, name ASC | Highest risk first; deterministic tiebreak |
| Algorithm direction | Inverted (downward from tests) | O(T × callees) instead of O(N × BFS); orders of magnitude faster on large solutions |
| `maxDepth` | 3, hardcoded | Matches `find_tests_for_symbol`; coherent mental model |
| `riskThreshold` | 5, hardcoded | McCabe industry standard |
| No env vars / no config file | Confirmed | Project has zero of these today; YAGNI |
| `ClassifyAsTest` | Extract to shared helper | Prevents drift between the two test-aware tools |
| Project filter | Skip in v1 | Whole-solution affordable with inverted algorithm; add filter only on demand |
