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
