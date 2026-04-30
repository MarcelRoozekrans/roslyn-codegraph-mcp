---
name: roslyn-codelens
description: Use when working with any .NET / C# code (.cs/.csproj/.sln/.slnx files), finding callers/references/implementations, checking compiler errors or warnings, running dotnet build for diagnostics, searching for a type/method/interface by name, inspecting DI registrations, detecting dead code or circular dependencies, inspecting source-generator output, or about to Grep/Glob across a C# codebase — activates when roslyn-codelens MCP tools are available.
---

# Roslyn CodeLens — Semantic .NET Intelligence

## Detection

If `find_implementations` is not available as an MCP tool, this skill is inert — do nothing, no errors.

If it IS available, every rule below applies. No exceptions.

## The Iron Law

**On a .NET codebase where roslyn-codelens MCP tools are available:**

1. **Never** use `Grep`, `Glob`, or Bash `grep`/`rg` to locate C# symbols, types, methods, interfaces, references, callers, implementations, or usages.
2. **Never** run `dotnet build`, `dotnet msbuild`, `msbuild`, or any build command to surface compiler errors, warnings, or analyzer diagnostics.
3. **Never** manually read a `.cs` file to "grep in my head" for who uses a symbol, or to check if code compiles.

The semantic tools (`find_callers`, `find_references`, `find_implementations`, `search_symbols`, `get_diagnostics`, `go_to_definition`, etc.) are **always** more accurate than text search and **always** faster than a build. There is no tradeoff to weigh.

**Violating the letter of these rules is violating the spirit.** If you're about to run a command or tool that *substitutes* for one of these semantic tools, stop.

## Red Flags — STOP and Use the MCP Tool

If any of these thoughts cross your mind, stop and switch to the MCP tool:

| Thought / Action | STOP — Use instead |
|---|---|
| "Let me `Grep` for `class Foo`" | `search_symbols` or `go_to_definition` |
| "Let me `Grep` for `Foo\\.Bar\\(`" (finding callers) | `find_callers` |
| "Who subscribes to this event?" / "Find += sites" / "Are we leaking event subscriptions?" | `find_event_subscribers` |
| "What does this method end up calling?" / "Show me the transitive callees" / "What's the blast radius for changing X?" | `get_call_graph` |
| "Let me `Grep` for `: IFoo`" (finding implementations) | `find_implementations` |
| "Let me `Grep` for `new Foo(`" | `find_references` |
| "Let me `Grep` for `[Authorize]`" | `find_attribute_usages` |
| "I'll run `dotnet build` to see errors" | `get_diagnostics` |
| "I'll run `dotnet build -warnaserror` to find warnings" | `get_diagnostics` |
| "Let me `Read` the .cs file to see what's defined" | `get_file_overview` or `get_type_overview` |
| "Let me `Glob` for `**/*Service.cs`" | `search_symbols` with a partial name |
| "Let me `Grep` for `Activator.CreateInstance`" | `find_reflection_usage` |
| "I'll check if this method is used by reading files" | `find_callers` / `find_unused_symbols` |
| "I'll eyeball complexity by reading the method" | `get_complexity_metrics` |
| "How is this project doing?" / "Where should I focus?" / "Show me the technical debt picture" | `get_project_health` |
| "Let me `Grep` for tests that call this method" | `find_tests_for_symbol` |
| "Which tests will break if I change this?" | `find_tests_for_symbol` |
| "What should I write tests for?" / "Where's our testing debt?" / "Show me untested public methods" | `find_uncovered_symbols` |
| "What's the public API of this library?" / "Show me the API surface" / "I need a PublicAPI.txt-style snapshot" | `get_public_api_surface` |
| "Will this break consumers?" / "Show me breaking changes vs the previous release" / "Diff this build's API against baseline" | `find_breaking_changes` |
| "Are there async bugs?" / "Find sync-over-async" / "Are we using `.Result` anywhere?" | `find_async_violations` |
| "Are there resource leaks?" / "Find missing `using`" / "Is this disposable handled?" | `find_disposable_misuse` |

**All of these mean: the MCP tool is the correct tool. Use it.**

## Rationalizations — and why they're wrong

| Excuse | Reality |
|---|---|
| "Just a quick Grep — it's faster." | It isn't. One `find_references` beats iterating Grep + reading matches + deduping false positives. |
| "Grep as a sanity check on top of the MCP tool." | Redundant and misleading. Grep finds comments, strings, partial matches. If Roslyn says there are N references, there are N references. |
| "This is just a string/literal search, so Grep is fine." | If the target is a C# symbol, it's not "just a string" — it has a definition, scope, and binding. Use `find_references`. String literal searches in comments/docstrings are the *only* legitimate Grep case. |
| "`dotnet build` is how everyone checks errors." | Not here. `get_diagnostics` returns compiler errors + analyzer results, structured, without rebuilding. A build is minutes; the tool is milliseconds. |
| "The MCP server might be slow / might fail." | If it fails, report that and ask. Do not silently fall back to Grep — that produces wrong answers that look right. |
| "I need to see the file contents anyway." | `get_file_overview`, `get_type_overview`, and `analyze_method` give you structure without a `Read`. Use `Read` only after you know which lines matter. |
| "The user asked me to Grep." | If the user asked for *a grep* specifically, ask if they actually want semantic results. If they asked for *information about the code* and suggested grep, use the semantic tool and tell them why. |
| "I'm just looking for TODO comments / string literals." | Fine — Grep is legitimate for comments, string literals, and non-C# files. That's the only free pass. |

## Pre-Action Checklist

**Before calling `Grep` or `Glob` on `.cs` / `.csproj` / `.sln` / `.slnx` / `.cshtml` files:**
1. Is the target a C# symbol (type/member/namespace)? → Use `search_symbols` or `find_references`.
2. Is the target an attribute? → Use `find_attribute_usages`.
3. Is the target a reflection pattern? → Use `find_reflection_usage`.
4. Is it *genuinely* a string literal, comment, or non-semantic text? → Grep is OK. State why.

**Before running `dotnet build` / `msbuild` via Bash:**
1. Am I looking for errors, warnings, or analyzer diagnostics? → Use `get_diagnostics`. Stop.
2. Am I actually trying to produce a binary / run tests / package? → Build is OK. State why.

**Before `Read`ing a `.cs` file:**
1. Do I just need structure (what's in it, what's defined)? → `get_file_overview` / `get_type_overview`.
2. Do I need a specific method's shape? → `analyze_method`.
3. Do I need the actual source lines to edit? → `Read` is OK.

## When to Use Each Tool

### Decision Tree for External Assemblies

```
I want to...                         Tool / Approach
─────────────────────────────────────────────────────────────────────
Work with types in my source code  → existing tools (unchanged)
Look up an external type by name   → go_to_definition / get_symbol_context /
                                     get_type_overview / get_type_hierarchy
                                     (pass fully-qualified name; returns origin="metadata")
Browse what a package exposes      → inspect_external_assembly
Who in my code uses an ext. type?  → find_references / find_callers / find_implementations
See a method's IL bytecode         → peek_il (pass fully-qualified method name with param types)
Inspect an arbitrary DLL           → add a <ProjectReference> to a throwaway
                                     project, rebuild_solution, then use normally
```

### Understanding a Codebase
- `get_project_dependencies` — solution architecture, how projects relate.
- `get_symbol_context` — full context for a type (namespace, base, interfaces, DI deps, public members).
- `get_public_api_surface` — Enumerate every public/protected type and member declared in production projects; deterministically sorted; suitable for API review or breaking-change baselines. Static analysis; only declared (not inherited) members appear.
- `find_breaking_changes` — Diff the current public API surface against a baseline (JSON snapshot from `get_public_api_surface`, or a `.dll` file). Reports Removed/Added/KindChanged/AccessibilityNarrowed/Widened with Breaking/NonBreaking severity. Static analysis; doesn't currently detect return-type, sealed-ness, or nullable changes.
- `get_type_hierarchy` — inheritance chains and extension points.
- `get_type_overview` — one-shot: context + hierarchy + diagnostics (replaces 3 calls).
- `get_file_overview` — types defined in a file + diagnostics, without reading it.
- `analyze_method` — signature + callers + outgoing calls, all in one.
- `get_call_graph` — Transitive caller/callee graph for a method, depth-bounded with cycle detection. Adjacency-list output. Use when you need depth > 1 (`analyze_method` is depth=1).

### Navigating Code (**instead of Grep/Glob**)
- `go_to_definition` — jump to the definition.
- `search_symbols` — fuzzy symbol lookup.
- `find_references` — every reference across the solution.

### Finding Dependencies and Usage
- `find_callers` — every call site for a method.
- `find_event_subscribers` — every += / -= site for an event symbol, with resolved handler name and subscribe/unsubscribe tag. Use for UI-event audits or memory-leak hunts (compare subscribe/unsubscribe pairs).
- `find_implementations` — all implementors of an interface / extenders of a class.
- `find_tests_for_symbol` — xUnit/NUnit/MSTest methods that exercise a production symbol; opt-in transitive walk through helpers.
- `find_uncovered_symbols` — Public methods and properties no test transitively reaches (≤ 3 helper hops); sorted by cyclomatic complexity for prioritization. Strict reference-based: an override is not marked covered just because its base or interface declaration is reached — a test calling `IFoo.Bar` does not cover `Foo.Bar`.
- `get_di_registrations` — DI wiring and lifetimes.
- `find_reflection_usage` — hidden/dynamic coupling (`Activator.CreateInstance`, `MethodInfo.Invoke`, assembly scanning).
- `get_nuget_dependencies` — NuGet packages and versions.
- `find_attribute_usages` — members decorated with a given attribute.

### Diagnostics (**instead of `dotnet build`**)
- `get_diagnostics` — compiler errors, warnings, analyzer diagnostics. Replaces `dotnet build` output.
- `get_code_fixes` — structured edits for a diagnostic.
- `get_code_actions` — all refactorings/fixes at a position (with optional range).
- `apply_code_action` — execute a refactoring by title. Preview mode by default.
- `analyze_data_flow` — variable lifecycle over a statement range (declared/read/written/captured/flows-in/out).
- `analyze_control_flow` — reachability, returns, unreachable paths.

**Code generation is in `apply_code_action`** — do NOT look for dedicated generation tools. Use `get_code_actions` to find the title, then `apply_code_action`:
- Implement missing interface/abstract members → *"Implement abstract members"* / *"Implement interface"*
- Generate constructor from fields → *"Generate constructor"*
- Add null checks → *"Add null checks for all parameters"*
- Generate `Equals`/`GetHashCode` → *"Generate Equals and GetHashCode"*
- Encapsulate field → *"Encapsulate field"*
- Extract method → *"Extract method"*
- Inline variable → *"Inline variable"*

### Code Quality Analysis
- `find_unused_symbols` — dead code (reference-based).
- `get_complexity_metrics` — cyclomatic complexity per method.
- `find_naming_violations` — .NET naming conventions.
- `find_async_violations` — Detects sync-over-async (`.Result`/`.Wait()`/`GetAwaiter().GetResult()`), `async void` outside event handlers, missing awaits in async methods, and fire-and-forget tasks. Severity error/warning per violation. Static analysis; no runtime data.
- `find_disposable_misuse` — Detects `IDisposable`/`IAsyncDisposable` instances not wrapped in `using`/`await using`/returned/assigned-to-field-or-out-parameter (warning), and bare-expression-statement discards of a disposable creator/factory (error). Static analysis; no runtime data.
- `find_large_classes` — oversized types.
- `find_circular_dependencies` — project/namespace cycles.
- `get_project_health` — Composite audit: complexity, large classes, naming, unused, reflection, async violations, disposable misuse — counts + top-N hotspots per dimension, per project. Use when you'd otherwise call 7 separate audit tools.

### Source Generators
- `get_source_generators` — list generators and their outputs.
- `get_generated_code` — inspect generated source (filter by generator or file path).

### Working with External Assemblies (Closed-Source / NuGet)

External symbols have `origin.kind = "metadata"` in tool results. Supply fully-qualified names (e.g. `Microsoft.Extensions.DependencyInjection.IServiceCollection`) to the Tier-1 tools below — they fall back to metadata lookup automatically when no source match is found.

- `inspect_external_assembly` — browse a referenced assembly's public API:
  - `mode="summary"` → namespace tree + type counts (start here to orient yourself)
  - `mode="namespace"` → full type + member listing for one namespace
- `peek_il` — read ilasm-style IL for a single method in a referenced assembly:
  - Input: fully-qualified method name with parameter types (e.g. `Namespace.Type.Method(ParamType1, ParamType2)`)
  - For constructors: use `..ctor` notation (e.g. `Namespace.Type..ctor(ParamType)`)
  - Output: raw IL instructions (`IL_0000: ldarg.0`, etc.) — not decompiled C#
  - When to use: after `find_callers` / `get_symbol_context` identifies an interesting external method and you want to understand its implementation

**Worked example — drill into a NuGet package:**
```
inspect_external_assembly(assemblyName: "Microsoft.Extensions.DependencyInjection.Abstractions", mode: "summary")
→ shows NamespaceTree with Microsoft.Extensions.DependencyInjection (15 types)

inspect_external_assembly(assemblyName: "Microsoft.Extensions.DependencyInjection.Abstractions",
    mode: "namespace", namespaceFilter: "Microsoft.Extensions.DependencyInjection")
→ returns IServiceCollection, ServiceDescriptor, ServiceLifetime, etc. with members and XML doc summaries
```

**To find where your code uses an external symbol:**
Use `find_references` / `find_callers` / `find_implementations` with the fully-qualified external symbol name. Results will be source locations in your codebase.

**To find all source files using `IServiceCollection`:**
```
find_references("Microsoft.Extensions.DependencyInjection.IServiceCollection")
→ returns source locations (all origin.kind="source") where your code references IServiceCollection

find_callers("Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton")
→ returns source call sites for AddSingleton (pass the extension class + method name)
```

**To read the raw IL of an external method:**
Use `peek_il` with the fully-qualified method name including parameter types:
```
peek_il("Microsoft.Extensions.DependencyInjection.ServiceDescriptor..ctor(System.Type, System.Type, Microsoft.Extensions.DependencyInjection.ServiceLifetime)")
→ returns ilasm-style IL text, assembly name, and version
```
Limitations: abstract methods, interface instance members, and properties-as-whole (target the getter/setter accessor instead) are not supported.

### Solution Management
- `list_solutions` — loaded solutions, which is active.
- `set_active_solution` — switch active solution by partial name.
- `load_solution` — load a `.sln`/`.slnx` at runtime.
- `unload_solution` — free memory.
- `rebuild_solution` — full reload (after `Directory.Build.props` changes, new analyzers/packages, or stale diagnostics).

### Planning a Change — standard order
1. `get_type_overview` — context + hierarchy + diagnostics.
2. `analyze_change_impact` — blast radius (files, projects, call sites).
3. `find_references` / `find_callers` / `find_implementations` — detailed dependency breakdown.
4. `get_project_dependencies` — architectural position.
5. `get_di_registrations` — wiring.
6. `find_reflection_usage` — dynamic coupling.
7. `find_attribute_usages` — attribute-driven behavior.
8. `get_diagnostics` — existing issues.
9. `get_code_fixes` / `get_code_actions` → `apply_code_action` — auto-fixes and refactorings.
10. `find_unused_symbols` — dead code to delete instead of refactor.
11. `get_complexity_metrics` — complexity hotspots.

Reference concrete types, interfaces, and call sites in your analysis. Not *"the services that implement this"* but *"These 3 classes implement `IUserService`: `UserService`, `CachedUserService`, `AdminUserService`."*

## Tool Quick Reference

| Tool | When to Use |
|------|-------------|
| `find_implementations` | "What implements this interface?" / "What extends this class?" |
| `find_callers` | "Who calls this method?" / "What depends on this?" |
| `find_event_subscribers` | "Who subscribes to this event?" |
| `find_references` | "Where is this symbol used?" / "Show all references" |
| `find_tests_for_symbol` | "What tests cover this method?" / "Which tests will break if I change X?" |
| `find_uncovered_symbols` | "What should I write tests for?" / "Where's our testing debt?" |
| `go_to_definition` | "Where is this defined?" / "Jump to source" |
| `search_symbols` | "Find types/methods matching this name" |
| `get_type_hierarchy` | "What's the inheritance chain?" |
| `get_symbol_context` | "Give me everything about this type" |
| `get_public_api_surface` | "What's the public API of this library?" |
| `find_breaking_changes` | "Will this break consumers?" |
| `get_di_registrations` | "How is this wired up?" / "What's the DI lifetime?" |
| `get_project_dependencies` | "How do projects relate?" |
| `get_nuget_dependencies` | "What packages does this project use?" |
| `find_reflection_usage` | "Is this used dynamically?" |
| `find_attribute_usages` | "What's marked [Obsolete]?" / "Find all [Authorize] controllers" |
| `get_diagnostics` | "Any compiler errors?" / "Show warnings" |
| `get_code_fixes` | "How do I fix this warning?" |
| `get_code_actions` | "What refactorings are available here?" |
| `apply_code_action` | "Apply this refactoring" / "Extract method" |
| `find_unused_symbols` | "Any dead code?" |
| `get_complexity_metrics` | "Which methods are too complex?" |
| `find_naming_violations` | "Check naming conventions" |
| `find_async_violations` | "Are there async bugs?" / "Find sync-over-async" |
| `find_disposable_misuse` | "Are there resource leaks?" / "Find missing `using`" |
| `find_large_classes` | "Find classes that need splitting" |
| `find_circular_dependencies` | "Any circular dependencies?" |
| `get_project_health` | "How is this project doing?" / "Top hotspots across all dimensions" |
| `get_source_generators` | "What source generators are active?" |
| `get_generated_code` | "Show generated code" |
| `inspect_external_assembly` | "What does this NuGet package expose?" / "Show me the API of X assembly" |
| `peek_il` | "Show IL for this method" / "What does this external method do at bytecode level?" |
| `list_solutions` | "What solutions are loaded?" |
| `load_solution` | "Load this .sln / .slnx at runtime" |
| `unload_solution` | "Free memory for this solution" |
| `set_active_solution` | "Switch to project B" |
| `rebuild_solution` | "Reload the solution" / "Diagnostics are stale" |
| `analyze_data_flow` | "What variables are read/written here?" |
| `analyze_control_flow` | "Is this code reachable?" |
| `analyze_change_impact` | "What breaks if I change this?" |
| `get_type_overview` | "Give me everything about this type in one call" |
| `analyze_method` | "Show signature, callers, and outgoing calls" |
| `get_call_graph` | "Transitive callers/callees, depth-bounded" |
| `get_file_overview` | "What types are in this file?" |

## Legitimate Grep / Build Exceptions

Grep is the correct tool for:
- Non-C# files (`.json`, `.yaml`, `.md`, `.razor` template text, shell scripts).
- String literals and comments inside C# code.
- Text that isn't a symbol (log messages, error strings, TODOs).

`dotnet build` is the correct command for:
- Actually producing a binary.
- Running tests (`dotnet test`) — not covered by this skill.
- Packaging / publishing.

State the reason when you take the exception. If you're about to type a Grep/Glob/build command and can't state the reason out loud, you're rationalizing — use the MCP tool.

## Metadata Support by Tool

| Tool | Works on metadata symbols | Caveats | Alternative |
|------|--------------------------|---------|-------------|
| `go_to_definition` | Yes — returns `File=""`, `Line=0` with `origin` block | No source location; use to confirm identity | |
| `get_symbol_context` | Yes — members, interfaces, base type | `InjectedDependencies` always empty | |
| `get_type_overview` | Yes | Diagnostics always empty | |
| `get_type_hierarchy` | Yes — base chain from metadata; derived types from source only | Cannot enumerate all ecosystem implementors | |
| `find_attribute_usages` | Yes — resolves metadata attribute type, returns source usages | | |
| `search_symbols` | Yes — includes metadata types (budget heuristic: BCL skipped when source hits exist) | May miss BCL types if source has matches | Use fully-qualified name with `go_to_definition` |
| `find_references` | Yes — finds source usages/references of external symbols | | |
| `find_callers` | Yes — finds source invocations of external methods | | |
| `find_implementations` | Yes — finds source implementors of external interfaces/classes | | |
| `inspect_external_assembly` | Metadata only — this is its purpose | Assembly must be referenced by a project in the solution | `get_nuget_dependencies` to discover assembly names |
| `peek_il` | Metadata only — this is its purpose | Abstract methods and interface instance members not supported | Use `go_to_definition` to confirm the method exists first |
| `get_diagnostics` | No — source only | | |
| `get_code_fixes` | No — source only | | |
| `get_code_actions` | No — source only | | |
| `apply_code_action` | No — source only | | |
| `analyze_data_flow` | No — source only | | |
| `analyze_control_flow` | No — source only | | |
| `analyze_change_impact` | No — source only | | |
| `analyze_method` | No — source only | | |
| `get_file_overview` | No — source only | | |
| `find_unused_symbols` | No — source only | | |
| `get_complexity_metrics` | No — source only | | |
| `find_naming_violations` | No — source only | | |
| `find_large_classes` | No — source only | | |
| `find_circular_dependencies` | No — source only | | |
| `get_source_generators` | No — source only | | |
| `get_generated_code` | No — source only | | |
| `get_di_registrations` | No — source only | | |
| `get_nuget_dependencies` | Partial — lists referenced packages, not assemblies directly | Use `inspect_external_assembly` for assembly API | |
| `find_reflection_usage` | No — source only | | |
| `list_solutions` | N/A | | |
| `set_active_solution` | N/A | | |
| `load_solution` | N/A | | |
| `unload_solution` | N/A | | |
| `rebuild_solution` | N/A | | |
