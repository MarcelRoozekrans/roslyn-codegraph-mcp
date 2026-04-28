# `find_async_violations` — Design

**Date:** 2026-04-28
**Backlog source:** `docs/BACKLOG.md` § 3 (async & concurrency tools)

## Goal

Detect six classes of async/await misuse across all production projects in the active solution. Surface each violation with a stable pattern enum, severity, location, containing method, and a short snippet — enough for an agent to triage and fix.

This is a static-analysis tool. No runtime instrumentation, no fix suggestions. Reports what's wrong; the agent decides what to do.

## Why

Async sins cause real production deadlocks (`.Result`, `.Wait()` on `SynchronizationContext`-using stacks), unobserved exceptions (`async void` outside event handlers), and silent test failures (missing `await` in async methods). These are universally recognized anti-patterns. No other code-graph MCP tool surfaces them today, and the .NET analyzers that catch some of them aren't on by default in every project.

---

## Tool surface

```
find_async_violations()
```

No parameters in v1. Whole-solution scope, hardcoded scope (skip test projects, skip generated code).

### Output

```jsonc
{
  "summary": {
    "totalViolations": 23,
    "byPattern": {
      "SyncOverAsyncResult": 5,
      "SyncOverAsyncWait": 2,
      "SyncOverAsyncGetAwaiterGetResult": 1,
      "AsyncVoid": 2,
      "MissingAwait": 3,
      "FireAndForget": 10
    },
    "bySeverity": { "error": 10, "warning": 13 }
  },
  "violations": [
    {
      "pattern": "SyncOverAsyncResult",
      "severity": "error",
      "filePath": "OrderService.cs",
      "line": 42,
      "containingMethod": "OrderService.Submit",
      "project": "MyApp.Core",
      "snippet": "result.Result"
    },
    ...
  ]
}
```

Sort: `severity` DESC (errors first), then `filePath` ASC, then `line` ASC.

`snippet` is the violation expression as source text, capped at ~80 chars.

---

## Six patterns

### 1. `SyncOverAsyncResult` — severity `error`

**Pattern:** `someTask.Result` access — synchronous wait that risks deadlock.

**Detection:** walk every `MemberAccessExpressionSyntax` where `Name.Identifier.Text == "Result"`. Use `SemanticModel.GetTypeInfo(expression.Expression)` to check the receiver type. If `Type` is `System.Threading.Tasks.Task` or `System.Threading.Tasks.Task<T>` (constructed-from), flag.

### 2. `SyncOverAsyncWait` — severity `error`

**Pattern:** `someTask.Wait()`, `Task.WaitAll(...)`, `Task.WaitAny(...)`.

**Detection:** walk every `InvocationExpressionSyntax`. Resolve to `IMethodSymbol`. If method's containing type is `Task` (instance `Wait`) or static method `Task.WaitAll` / `Task.WaitAny`, flag.

### 3. `SyncOverAsyncGetAwaiterGetResult` — severity `error`

**Pattern:** `someTask.GetAwaiter().GetResult()` — common idiom to dodge async, equally bad.

**Detection:** walk every `InvocationExpressionSyntax` with `MemberAccessExpressionSyntax` named `.GetResult()`. Resolve. If receiver type is `TaskAwaiter`, `TaskAwaiter<T>`, `ConfiguredTaskAwaitable.ConfiguredTaskAwaiter`, or `ConfiguredTaskAwaitable<T>.ConfiguredTaskAwaiter`, flag.

### 4. `AsyncVoid` — severity `error`

**Pattern:** `async void` method declaration outside the event-handler exemption.

**Detection:** walk every `MethodDeclarationSyntax`. If `Modifiers` contains `async` AND `ReturnType` is `void` AND method does **not** match the event-handler signature, flag.

**Event-handler exemption:** signature is `(object sender, T e)` where `T` is `EventArgs` or any type derived from it. Also accept `(object? sender, T? e)` (nullable variants). Two-parameter methods that don't match this exact shape are flagged.

### 5. `MissingAwait` — severity `warning`

**Pattern:** inside an `async Task` / `async Task<T>` method body, a `Task`-returning call used as a bare expression statement (result implicitly discarded). The author probably meant to await it.

**Detection:** for each method with `async` modifier and a Task-shaped return type, walk its body's `ExpressionStatementSyntax` nodes. If the expression is an `InvocationExpressionSyntax` whose return type (per `SemanticModel`) is `Task` / `Task<T>` / `ValueTask` / `ValueTask<T>`, flag.

(This is the high-noise pattern but generally indicative of real bugs in async methods. Sync methods that intentionally return `Task` are excluded from the walk.)

### 6. `FireAndForget` — severity `warning`

**Pattern:** in a **non-async** method body, a `Task`-returning call used as a bare expression statement, NOT prefixed by `_ = ` or assigned to a variable.

**Detection:** same as `MissingAwait`, but only when the containing method is **not** `async`. Bare `_ = SomeAsyncMethod()` or `var t = SomeAsyncMethod()` are explicit acknowledgments and skipped.

---

## Algorithm shape

Single pass over non-test compilations. Per syntax tree:

1. For each `MethodDeclarationSyntax` in the tree:
   - Check the method itself for `AsyncVoid` (rule 4).
   - Walk its body for invocations / member accesses to detect rules 1–3, 5, 6.
2. Record violations with their location, pattern, severity, containing method, project.

Roslyn's `SemanticModel.GetTypeInfo` and `SemanticModel.GetSymbolInfo` are the workhorses — both run in microseconds and are cached internally.

Performance shape: roughly equivalent to `find_callers` (linear in syntax-tree size, semantic-model lookups per node of interest).

---

## Hardcoded constants

| Constant | Value | Why hardcoded |
|----------|------:|---------------|
| Severity per pattern | error / warning per the table above | Clear-cut categorization; no team disagreement on these. |
| Snippet length | ~80 chars | Enough context, doesn't bloat output. |
| Skip test projects | true | Tests legitimately use various async patterns; reporting them produces noise. |
| Skip compiler-generated | true | `IsImplicitlyDeclared`. |
| Skip auto-generated source | true | `<auto-generated>` header detection. |

No tool parameters, no env vars, no config file. Same justification as the rest of this codebase.

---

## Reuse

| Component | Source | Action |
|-----------|--------|--------|
| `TestProjectDetector` | shipped earlier | Reuse for the production-only filter. |
| `LoadedSolution` / `SymbolResolver` | core | Reuse the existing `(projectId, compilation)` iteration pattern. |
| Auto-generated source detection | none yet | Small helper: read first ~5 lines of source tree, check for `<auto-generated>` marker. |

---

## Edge cases

| Case | Behaviour |
|------|-----------|
| `await someTask` | Not flagged (not a violation). |
| `_ = someAsyncMethod()` | Not flagged (explicit fire-and-forget acknowledgment). |
| `var t = someAsyncMethod()` (assigned but never awaited) | Not flagged at the call site — the variable is "tracked", and we don't do flow analysis. |
| `return someAsyncMethod()` in a non-async `Task`-returning method | Not flagged (perfectly valid pattern — the caller awaits). |
| `Task.Run(() => task.Result)` | The inner `.Result` IS flagged. Even on a thread-pool thread, `.Result` blocks; not idiomatic. |
| Conversion operators / properties / indexers / accessors | Walked the same way as methods (Roslyn's `MethodDeclarationSyntax` covers methods only; we should also walk `AccessorDeclarationSyntax` and arrow-bodied members). v1: only `MethodDeclarationSyntax`. Property / accessor support deferred. |
| Local functions | Walked as part of containing method's body. Their violations attribute to the containing method (acceptable for v1; agents can drill in if needed). |
| Lambdas (`Action`, `Func<Task>`, etc.) | Same — walked as part of containing method. |
| Async methods called from `Main` | Reported normally (Main is just another method). If the project's `Main` legitimately uses `.Result` or `.Wait()`, the agent suppresses by ignoring. |
| Top-level statements | The compiler synthesises a `<Main>$` method; its violations are reported. Containing method shows as `Program.<Main>$` or similar — fine. |

---

## Tests

### Fixture additions

Add a new project `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/AsyncFixture/`:

- `AsyncFixture.csproj` — minimal SDK-style project, no test framework reference (so it counts as a production project for `TestProjectDetector`).
- `Violations.cs` — one method per positive case for each of the 6 patterns. Plus negative cases:
  - A properly-awaited task call (negative for MissingAwait).
  - An `async void` event handler (negative for AsyncVoid — must NOT flag).
  - A `_ = SomeAsync()` discard (negative for FireAndForget).
  - A `var t = SomeAsync()` variable assignment (negative for FireAndForget).
  - A non-async `Task`-returning method that does `return SomeAsync();` (negative for FireAndForget — perfectly valid forwarding pattern).

Update `TestSolution.slnx` to include the new project.

### Test cases

| File | What it covers |
|------|---------------|
| `Tools/FindAsyncViolationsToolTests.cs` | One [Fact] per pattern asserting exactly-one positive hit. Negative-case assertions: properly-awaited tasks not flagged, event-handler async void not flagged, discards/assignments not flagged. Summary count tests: byPattern correct, bySeverity correct, totalViolations matches list length. Sort-order test: errors before warnings, then file ASC, then line ASC. Test-project exclusion test: an `async void DisposeAsync()` in any of the test fixtures is NOT reported (test projects skipped). |

### Benchmark

Add to `CodeGraphBenchmarks.cs`:

```csharp
[Benchmark(Description = "find_async_violations: whole solution")]
public object FindAsyncViolations()
    => FindAsyncViolationsLogic.Execute(_loaded, _resolver);
```

---

## SKILL.md / README updates

**SKILL.md:** add a routing row in Red Flags:

> | "Are there async bugs?" / "Find sync-over-async" / "Are we using `.Result` anywhere?" | `find_async_violations` |

Add to "Diagnostics" or a new "Code-quality" section:

> - `find_async_violations` — Detects sync-over-async (`.Result`/`.Wait()`/`GetAwaiter().GetResult()`), `async void` outside event handlers, missing awaits in async methods, and fire-and-forget tasks. Reports per-violation with severity (error/warning).

**README:** add to Features list near `find_naming_violations`:

> - **find_async_violations** — Sync-over-async, `async void` misuse, missing awaits, fire-and-forget tasks; per-violation report with severity.

Tool count: 24 → 25 in `CLAUDE.md`.

---

## Out of scope (explicit non-goals)

- No `ConfigureAwait(false)` recommendations — modern .NET (ASP.NET Core, console apps, libraries targeting net6+) doesn't always benefit; reporting it would produce noise on contemporary codebases.
- No `Task.Run` on CPU-bound heuristics — distinguishing CPU-bound from I/O-bound code statically is unreliable.
- No custom-awaiter pattern detection (`AsyncMethodBuilder`, custom `INotifyCompletion`) — vanishingly rare in user code.
- No suppression mechanism (`[SuppressMessage]`, comment-based) — every existing tool in this repo reports everything; agents post-filter the JSON.
- No project / namespace filter — whole-solution; add only on demand.
- No fix suggestions — analysis only.
- No accessor / property / indexer body analysis — only `MethodDeclarationSyntax` in v1.
- No flow-sensitive analysis for "task assigned but never awaited" — once a Task is in a variable, we trust the user.

---

## Decisions log

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Pattern scope | Six (sync-over-async × 3, async void, missing await, fire-and-forget) | Drops controversial ConfigureAwait + heuristic Task.Run + rare custom awaiters; keeps every reported pattern unambiguously useful |
| Output shape | Summary + flat violations list | Matches `find_uncovered_symbols` precedent; summary lets agents triage in one glance |
| Severity split | Error for sync-over-async + AsyncVoid; warning for MissingAwait + FireAndForget | Errors are always-bugs; warnings are usually-bugs with documented intentional uses |
| Suppression | None in v1 | Consistent with existing tools; agents post-filter |
| Scope filter | None in v1 | Whole-solution; add on demand |
| Tool parameters | None | Project has zero per-tool tunables today; YAGNI |
| Test project handling | Skip via `TestProjectDetector` | Tests legitimately use async patterns; reporting them = noise |
| Accessor / property bodies | Skip in v1 | Most async sins live in regular methods; expand later if demand emerges |
