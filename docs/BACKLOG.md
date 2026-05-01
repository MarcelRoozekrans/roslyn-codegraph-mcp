# Backlog

Ideas for future tools, grouped by theme. Not committed work — captured here so they're not lost. Each entry is a starting point for a real design discussion.

---

## 1. Test-aware tools

Link production code to the tests that exercise it.

- **`find_tests_for_symbol`** — given a method/class, find xUnit/NUnit/MSTest methods whose bodies reference it (transitively, depth-bounded).
- **`find_uncovered_symbols`** — public members with zero references from any test project. Heuristic, not a substitute for real coverage data.
- **`get_test_summary`** — per project, list test methods with their targeting attributes (`[Theory]`, `[Fact]`, `[InlineData]`, etc.) and the symbols they reference.

## 2. API & breaking-change tools

Useful for library authors and anyone reviewing a version bump.

- **`find_breaking_changes`** — compare the current build against a baseline assembly (NuGet version or path); report removed members, signature changes, accessibility narrowings, attribute changes.
- **`get_public_api_surface`** — full list of public types/members per project, suitable for an API review or `PublicAPI.txt`-style snapshot.
- **`find_obsolete_usage`** — specialised for `[Obsolete]` (richer than the generic `find_attribute_usages`): groups by deprecation message, flags whether each call site is still reachable.

## 3. Async & concurrency tools

Common .NET pain points that aren't covered by analyzers everyone has on by default.

- **`find_async_violations`** — sync-over-async (`.Result`, `.Wait()`, `.GetAwaiter().GetResult()`), missing `await`, `async void` outside event handlers, fire-and-forget tasks.
- **`find_disposable_misuse`** — `IDisposable` / `IAsyncDisposable` instances not wrapped in `using` / `await using`, including via factory returns.
- **`find_thread_safety_issues`** — lock usage patterns, shared mutable state in static fields, captured locals in tasks.

## 4. Navigation niceties

Small, focused queries that aren't currently expressible in one call.

- **`find_event_subscribers`** — for a given event, list every `+=` subscription site.
- **`get_overloads`** — all overloads of a method (signature + parameter docs side by side).
- **`get_call_graph`** — transitive caller and/or callee graph, depth-bounded, with cycle detection. Standalone version of what `analyze_method` does at depth=1.
- **`find_duplicated_code`** — heuristic detection of repeated statement blocks across files.

## 5. Generation & scaffolding (write-side)

Companions to `apply_code_action`, but for shapes that Roslyn doesn't ship out of the box.

- **`generate_test_skeleton`** — for a target method, emit an xUnit (configurable) test class with `[Fact]`/`[Theory]` stubs covering happy path + each thrown exception type.
- **`generate_dto_from_class`** — given a domain class, emit a DTO + AutoMapper-style mapping (or manual `ToDto`/`FromDto` extension methods).
- **`generate_builder`** — fluent builder for a class, including required-property tracking.

## 6. Project-health composites

Roll-ups that bundle existing analyses into one report so an agent can answer "how is this project doing?" in one call.

- **`get_project_health`** — composite of `get_complexity_metrics` + `find_naming_violations` + `find_large_classes` + `find_unused_symbols` + `find_reflection_usage`, summarised per project with hotspots highlighted.
- **`find_god_objects`** — beyond raw size from `find_large_classes`: factor in incoming coupling (callers across many namespaces), outgoing coupling, and field count to flag SRP-violating types.

---

## Deferred from shipped features

Items considered during design of shipped features and consciously punted on. Re-promote to the main backlog above if a use case emerges.

### From `find_obsolete_usage` (designed 2026-05-01)
- **Reachability analysis per call site** — whether each call site is reachable from a test or public entry point. `analyze_change_impact` already covers this; agent can compose.
- **Auto-migration suggestions** — agent's call; tool stays diagnostic, not prescriptive.
- **`DiagnosticId` / `UrlFormat` attribute properties** — promote if agents start asking for them.
- **Inherited deprecation propagation** — Roslyn doesn't propagate `[Obsolete]` to overrides; can be inferred via `find_implementations`.

### From `get_project_health` (shipped 2026-04-30)
- **Numeric "health score" or letter grade** — opinionated; agent computes client-side from counts.
- **Trend over time** — would require persistence layer.
- **Configurable dimension list** — YAGNI; agent calls underlying tool directly when it wants one dimension.

### From `find_god_objects` (in flight)
- **ML-based detection** — heuristic is enough.
- **Splitting / refactoring suggestions** — caller's judgment.
- **Reflection-coupling counted toward incoming-namespace tally** — separate concern.

### From `get_call_graph` (shipped 2026-04-29)
- **Edge-level annotations** (call-site location per edge) — would expand JSON significantly.
- **Direction-aware path computation server-side** — agent can derive from the adjacency list.
- **Method-group expressions** (`Action a = obj.Method;`) — only direct invocations are followed.
- **Async state-machine awaits** as a separate edge kind — currently grouped with method calls.

### From `find_breaking_changes` (shipped 2026-04-29)
- **Return-type changes** — `PublicApiEntry` schema doesn't capture them.
- **Sealed-ness changes** — same.
- **Nullable-annotation changes** — same.

### From `get_test_summary` (designed 2026-05-01)
- **Async-test flagging** — `IsAsync` could surface as a per-test field; not included now.
- **Skip-reason surface** for `[Fact(Skip = "…")]` / `[Ignore("…")]` — agent can compose via `find_attribute_usages` if needed.
- **`[MemberData]` / `[ClassData]` row tracking** — only inline `[InlineData]`/`[TestCase]`/`[DataRow]` are counted; data-source attributes don't expose row count without runtime evaluation.
- **Cross-project test→production coverage map** — that's `find_tests_for_symbol` territory in reverse.
