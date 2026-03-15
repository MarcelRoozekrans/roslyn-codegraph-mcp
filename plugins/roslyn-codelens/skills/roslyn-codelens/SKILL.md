---
name: roslyn-codelens
description: Use Roslyn-powered semantic code intelligence when working with .NET codebases. Activates automatically when roslyn-codelens MCP tools are available.
---

# Roslyn Code Graph Intelligence

## Detection

Check if `find_implementations` is available as an MCP tool. If not, this skill is inert — do nothing.

## Critical Rules

1. **Never use `dotnet build`, `dotnet msbuild`, or MSBuild output** to find compiler errors, warnings, or code quality issues. Use `get_diagnostics` instead — it returns the same diagnostics (and more, including analyzer results) without a separate build step.
2. **Never use Grep/Glob** for navigating .NET codebases when these tools are available. They provide semantic accuracy that text search cannot match — finding true callers, implementations, and references rather than string matches.
3. **Always prefer these MCP tools first** for any .NET code understanding, navigation, or quality task. Only fall back to Grep/Glob for non-.NET files or when searching for string literals/comments.

## When to Use These Tools

Use these tools **instead of Grep/Glob and instead of MSBuild** whenever you need to understand .NET code structure, find issues, or navigate the codebase.

### Understanding a Codebase

- Call `get_project_dependencies` to understand solution architecture and how projects relate
- Call `get_symbol_context` on types mentioned in the user's request for a full context dump (namespace, base class, interfaces, DI dependencies, public members)
- Call `get_type_hierarchy` to understand inheritance chains and extension points
- Call `get_type_overview` for a one-shot view of a type: context + hierarchy + file diagnostics (replaces 3 separate calls)
- Call `get_file_overview` to see which types are defined in a file and any diagnostics — without reading the file
- Call `analyze_method` to get signature, callers, and outgoing calls for a method in one call

### Navigating Code

**Do NOT use Grep/Glob to find .NET types, methods, or usages.** Text search gives false positives (comments, strings, partial matches). These tools understand the actual code:

- Use `go_to_definition` to jump to where a type or member is defined — replaces Grep/Glob file search
- Use `search_symbols` to fuzzy-find types, methods, properties, and fields by name — replaces `Grep` for symbol lookup
- Use `find_references` to find every reference to a symbol across the entire solution — replaces `Grep` for usage search

### Finding Dependencies and Usage

- Use `find_callers` to find every call site for a method — more accurate than text search
- Use `find_implementations` to find all classes implementing an interface or extending a class
- Use `get_di_registrations` to see how types are wired in the DI container and their lifetimes
- Use `find_reflection_usage` to detect hidden/dynamic coupling that text search misses (Type.GetType, Activator.CreateInstance, MethodInfo.Invoke, assembly scanning)
- Use `get_nuget_dependencies` to see what NuGet packages a project uses and their versions
- Use `find_attribute_usages` to find all types/members decorated with a specific attribute (e.g., Obsolete, Authorize, Serializable)

### Diagnosing Issues

**Do NOT run `dotnet build` or `dotnet msbuild` to check for errors/warnings.** These MCP tools provide the same diagnostics plus analyzer results, structured as data you can act on:

- Use `get_diagnostics` to list compiler errors, warnings, and Roslyn analyzer diagnostics across the solution — this replaces `dotnet build` output entirely
- Use `get_code_fixes` to get structured text edits for fixing a specific diagnostic — review and apply via Edit tool (replaces manually interpreting build warnings)
- Use `get_code_actions` to discover all available refactorings and fixes at a specific position (e.g., extract method, rename, inline variable). Optionally select a range with endLine/endColumn for extract-style operations
- Use `apply_code_action` to execute a refactoring by its title (from `get_code_actions`). Defaults to preview mode — returns a diff without writing. Set preview=false to apply to disk
- Use `analyze_data_flow` on a statement range to understand variable lifecycle (declared, read, written, captured, flows in/out) — useful before extracting code
- Use `analyze_control_flow` on a statement range to check reachability, return statements, and unreachable code paths

**Code generation via `apply_code_action`** — do NOT look for dedicated generation tools. These common tasks are all built-in Roslyn code actions; use `get_code_actions` to find the title, then `apply_code_action`:
- Implement missing interface/abstract members → position on the class, look for "Implement abstract members" or "Implement interface"
- Generate constructor from fields → position on the class, look for "Generate constructor"
- Add null checks → position on a method, look for "Add null checks for all parameters"
- Generate `Equals`/`GetHashCode` → position on the class, look for "Generate Equals and GetHashCode"
- Encapsulate field → position on a field, look for "Encapsulate field"
- Extract method → select statements, look for "Extract method"
- Inline variable → position on variable, look for "Inline variable"

### Code Quality Analysis

**Do NOT rely on MSBuild warnings or manual code inspection for quality checks.** Use these purpose-built tools:

- Use `find_unused_symbols` to detect dead code — types/members with no references
- Use `get_complexity_metrics` to find overly complex methods (cyclomatic complexity above threshold)
- Use `find_naming_violations` to check .NET naming convention compliance (PascalCase, camelCase, I-prefix, _ prefix)
- Use `find_large_classes` to identify types that may need refactoring (too many members or lines)
- Use `find_circular_dependencies` to detect project or namespace dependency cycles

### Source Generators

- Use `get_source_generators` to list source generators and their output per project (optional project filter)
- Use `get_generated_code` to inspect generated source code from source generators (filter by generator name or file path)

### Solution Management

- Use `list_solutions` to see all loaded solutions, which one is active, how many projects each has, and their status.
- Use `set_active_solution` to switch the active solution by partial name (e.g. `set_active_solution("ProjectB")`). Call this at the start of a session when multiple solutions are loaded.
- Use `rebuild_solution` to force a full reload of the analyzed solution — re-opens the `.sln`, recompiles all projects, and rebuilds all indexes. Use after changing `Directory.Build.props`, adding/removing NuGet packages or analyzers, or when diagnostics seem stale.

### Planning Changes

Before modifying code, use these tools to understand the impact:

1. `get_type_overview` — one-shot: context + hierarchy + diagnostics for the type
2. `analyze_change_impact` — blast radius: all files, projects, and call sites affected by changing a symbol
3. `find_references` + `find_callers` + `find_implementations` — detailed dependency breakdown if needed
4. `get_project_dependencies` — where does it sit in the architecture?
5. `get_di_registrations` — how is it wired up?
6. `find_reflection_usage` — is it used dynamically?
7. `find_attribute_usages` — are there attribute-driven behaviors (authorization, serialization, etc.)?
8. `get_diagnostics` — are there existing compiler/analyzer warnings to address?
9. `get_code_fixes` — can any warnings be auto-fixed?
9b. `get_code_actions` → `apply_code_action` — discover and apply refactorings (extract method, rename, inline, etc.)
10. `find_unused_symbols` — is there dead code to clean up?
11. `get_complexity_metrics` — are there overly complex methods?

Reference concrete types, interfaces, and call sites in your analysis. Example: "These 3 classes implement IUserService: UserService, CachedUserService, AdminUserService."

## Tool Quick Reference

| Tool | When to Use |
|------|-------------|
| `find_implementations` | "What implements this interface?" / "What extends this class?" |
| `find_callers` | "Who calls this method?" / "What depends on this?" |
| `find_references` | "Where is this symbol used?" / "Show all references" |
| `go_to_definition` | "Where is this defined?" / "Jump to source" |
| `search_symbols` | "Find types/methods matching this name" |
| `get_type_hierarchy` | "What's the inheritance chain?" / "What are the extension points?" |
| `get_symbol_context` | "Give me everything about this type" (one-shot) |
| `get_di_registrations` | "How is this wired up?" / "What's the DI lifetime?" |
| `get_project_dependencies` | "How do projects relate?" / "What's the dependency graph?" |
| `get_nuget_dependencies` | "What packages does this project use?" |
| `find_reflection_usage` | "Is this used dynamically?" / "Are there hidden dependencies?" |
| `find_attribute_usages` | "What's marked [Obsolete]?" / "Find all [Authorize] controllers" |
| `get_diagnostics` | "Any compiler errors?" / "Show warnings for this project" |
| `get_code_fixes` | "How do I fix this warning?" / "Get auto-fix suggestions" |
| `get_code_actions` | "What refactorings are available here?" / "Can I extract this method?" |
| `apply_code_action` | "Apply this refactoring" / "Extract method" / "Inline variable" |
| `find_unused_symbols` | "Any dead code?" / "What's not being used?" |
| `get_complexity_metrics` | "Which methods are too complex?" |
| `find_naming_violations` | "Check naming conventions" |
| `find_large_classes` | "Find classes that need splitting" |
| `find_circular_dependencies` | "Any circular dependencies?" |
| `get_source_generators` | "What source generators are active?" / "List generators for this project" |
| `get_generated_code` | "Show generated code" / "What did this generator produce?" |
| `list_solutions` | "What solutions are loaded?" / "Which solution is active?" |
| `set_active_solution` | "Switch to project B" / "Use the other solution" |
| `rebuild_solution` | "Reload the solution" / "Pick up new analyzers" / "Diagnostics are stale" |
| `analyze_data_flow` | "What variables are read/written here?" / "What flows out of this block?" |
| `analyze_control_flow` | "Is this code reachable?" / "Any unreachable statements?" |
| `analyze_change_impact` | "What breaks if I change this?" / "Show blast radius" |
| `get_type_overview` | "Give me everything about this type in one call" |
| `analyze_method` | "Show signature, callers, and outgoing calls for this method" |
| `get_file_overview` | "What types are in this file?" / "Any diagnostics in this file?" |
