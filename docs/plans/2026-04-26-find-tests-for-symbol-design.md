# `find_tests_for_symbol` — Design

**Date:** 2026-04-26
**Backlog source:** `docs/BACKLOG.md` § 1 (test-aware tools)

## Goal

Given any symbol — production method, class, property — list the tests that exercise it. Cover xUnit, NUnit, and MSTest. Default to direct callers only (token-light); allow opt-in transitive walk through helpers up to a bounded depth.

## Why

Every refactor pulls in the question "what tests will I need to update?" Today an agent has to call `find_callers` and post-filter by attribute, which is brittle (three frameworks use different markers) and can't follow helper-mediated chains (`Test → BuildOrder → CalculateTotal`). A first-class tool removes both gaps and composes naturally with `analyze_change_impact`.

---

## Tool surface

```
find_tests_for_symbol(
  symbol: string,
  transitive?: boolean = false,
  maxDepth?: int = 3       // only used when transitive=true; capped at 5
)
```

`symbol` uses the same format as `find_callers` — fully-qualified or `Class.Member` form.

### Output — default (direct only)

```json
{
  "symbol": "OrderService.CalculateTotal",
  "directTests": [
    {
      "fullyQualifiedName": "MyApp.Tests.OrderServiceTests.CalculatesTotalCorrectly",
      "framework": "xunit",
      "attribute": "Fact",
      "filePath": "tests/OrderServiceTests.cs",
      "line": 42,
      "project": "MyApp.Tests"
    }
  ]
}
```

### Output — `transitive: true`

Adds a `transitiveTests` array. Each entry has the same fields plus a `callChain` listing the intermediate method short names with the target last:

```json
{
  "fullyQualifiedName": "...",
  "framework": "...",
  "attribute": "Theory",
  "filePath": "...",
  "line": 99,
  "project": "...",
  "callChain": ["BuildOrder", "ApplyDiscount", "CalculateTotal"]
}
```

A test that's both a direct caller and a transitive caller (via another path) appears only in `directTests`. No duplicates.

---

## Recognition & scope

### Test attributes

Matched on attribute symbol's containing-namespace + name, not on C# syntax. So `[Test]` and `[NUnit.Framework.Test]` and `[NUnit.Framework.TestAttribute]` all resolve to the same recogniser.

| Framework | Recognised attributes |
|-----------|----------------------|
| xUnit | `Xunit.FactAttribute`, `Xunit.TheoryAttribute` |
| NUnit | `NUnit.Framework.TestAttribute`, `NUnit.Framework.TestCaseAttribute`, `NUnit.Framework.TestCaseSourceAttribute` |
| MSTest | `Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute`, `Microsoft.VisualStudio.TestTools.UnitTesting.DataTestMethodAttribute` |

### Test project detection

Auto-detected once per `LoadedSolution`. A project is a "test project" if any package reference name matches:

- `xunit*` (covers `xunit`, `xunit.v3`, `xunit.runner.*`)
- `NUnit*`
- `MSTest*`

Cached as `HashSet<ProjectId>` on `LoadedSolution`. Non-test projects are excluded from the call-graph walk to keep performance bounded.

### Edge cases

| Case | Behaviour |
|------|-----------|
| `[Theory]` / `[TestCase]` with N data rows | Reported once (the method), not per row. Enumerating rows would blow up token usage; agents can drill in with `get_symbol_context`. |
| Abstract base test class | Skipped — no concrete tests run from it. |
| `internal`/`private` test method | Reported (xUnit accepts non-public). |
| Cycle in the call graph (transitive mode) | Visited-set prevents infinite walk; first chain found wins. |
| Symbol not found | Standard error pointing at `search_symbols`. |
| Symbol exists but has zero callers | Empty `directTests: []`, no error. |
| `maxDepth` outside `[1, 5]` | Clamped to bounds. |

---

## Implementation

### New files

| File | Purpose |
|------|---------|
| `src/RoslynCodeLens/Tools/FindTestsForSymbolTool.cs` | MCP tool wrapper, parameter binding |
| `src/RoslynCodeLens/Tools/FindTestsForSymbolLogic.cs` | Resolution + walk + classification |
| `src/RoslynCodeLens/Models/TestReference.cs` | Output record + framework enum |
| `src/RoslynCodeLens/Tests/TestProjectDetector.cs` | Package-ref scan, cached on `LoadedSolution` |
| `src/RoslynCodeLens/Tests/TestAttributeRecognizer.cs` | Static FQN→framework lookup |

### Reuse

`FindCallersLogic` does the actual call-site discovery. The new tool layers on top:

- **Direct mode**: `FindCallersLogic.Execute(target)` → filter results to methods marked with a recognised test attribute → emit `directTests`.
- **Transitive mode**: BFS outward from `target`. For each frontier symbol, ask `FindCallersLogic` for its callers. If a caller is a test method → add to `transitiveTests` with the chain. Otherwise enqueue (if depth budget remains, and not already visited). Stop expanding past depth `maxDepth`.

The visited set is keyed on the caller's `IMethodSymbol` ID (Roslyn `SymbolEqualityComparer.Default`). Cycle in the call graph → already-visited node is skipped.

### Performance

- Direct mode: O(callers of target) — same upper bound as `find_callers`, plus a constant-time attribute lookup per caller.
- Transitive mode: O(visited × avg branching factor), bounded by `maxDepth`. Visited-set keeps it tractable on graphs with hubs (e.g. shared test helpers).

### MCP tool registration

Register in `Program.cs` alongside the existing tools. Tool description states "List the test methods that exercise the given production symbol; supports xUnit, NUnit, MSTest. Set `transitive: true` to follow helper methods up to `maxDepth` levels (default 3, max 5)."

---

## Tests & fixtures

### Fixture additions

The existing `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/` is xUnit-based. Add two more fixture projects so we can test all three frameworks:

- `Fixtures/TestSolution/NUnitFixture/` — references `NUnit`, contains a couple of `[Test]` methods calling into `TestLib`.
- `Fixtures/TestSolution/MSTestFixture/` — references `MSTest`, contains `[TestMethod]` methods calling into `TestLib`.

These keep the fixture solution self-contained (no NuGet feed beyond what the existing fixtures already use).

### Test cases

| File | What it covers |
|------|---------------|
| `Tools/FindTestsForSymbolToolTests.cs` | Direct hit (xUnit / NUnit / MSTest), transitive hit, depth-cap behaviour, cycle in call graph, no-callers, symbol-not-found, mixed-framework solution, `[Theory]` reported once, abstract base skipped. |
| `Tests/TestProjectDetectorTests.cs` | Package-ref pattern matching for each framework, mixed solution, project with no test packages excluded. |
| `Tests/TestAttributeRecognizerTests.cs` | Each recognised attribute resolves correctly; unrelated attributes don't match. |

### Benchmark

Add to `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`:

```csharp
[Benchmark(Description = "find_tests_for_symbol: IGreeter.Greet")]
public object FindTestsForSymbol()
    => FindTestsForSymbolLogic.Execute(_loaded, _resolver, _metadata, "IGreeter.Greet", transitive: false, maxDepth: 3);
```

### SKILL.md

Add to the routing table:

> | "What tests cover this method?" / "Which tests will break if I change X?" | `find_tests_for_symbol` |

---

## Out of scope (explicit non-goals)

- No coverage-data integration (no `coverlet`/`dotCover` parsing). The tool reports references, not runtime coverage.
- No bidirectional view (production code → tests, but not tests → production code). Use `analyze_method` on the test method for that.
- No theory-row enumeration. The method appears once.
- No new test framework support beyond xUnit / NUnit / MSTest. Adding more is additive (one entry in `TestAttributeRecognizer`).
- No "tests that *should* exist but don't" detection. That's `find_uncovered_symbols`, a separate backlog item.

---

## Decisions log

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Default mode | Direct only | Token-light; the common question is "show me the immediate tests" |
| Transitive opt-in | `transitive: true` | Don't pay the BFS cost unless asked |
| Default `maxDepth` | 3 | Real-world helper depth in tests is usually 1–2; 3 covers the long tail |
| `maxDepth` cap | 5 | Prevents runaway walks on deeply layered test infrastructure |
| Output detail | Minimal (FQN + file:line + framework + attribute + project) | Enough to navigate or run; richer detail is one `get_symbol_context` away |
| Frameworks | xUnit + NUnit + MSTest, fixed list | ~98% of .NET test code; configurable detection is over-engineering |
| Test project detection | Package-ref pattern scan, cached | Auto, no config file, fast |
| `[Theory]` rows | Method appears once | Per-row enumeration explodes output; agents can drill in |
| Direct + transitive overlap | Direct wins | No duplicates |
