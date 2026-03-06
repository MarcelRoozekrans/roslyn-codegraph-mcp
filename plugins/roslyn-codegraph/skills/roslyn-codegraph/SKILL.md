---
name: roslyn-codegraph
description: Enhances brainstorming and refactor-analysis with Roslyn-powered semantic code intelligence for .NET codebases. Activates automatically when roslyn-codegraph MCP tools are available.
---

# Roslyn Code Graph Intelligence

## Detection

Check if `find_implementations` is available as an MCP tool. If not, this skill is inert — do nothing.

## During Brainstorming

When brainstorming about a .NET codebase:

1. **At start** — Call `get_project_dependencies` on the main project to understand solution architecture. Call `get_symbol_context` on any types mentioned in the request.

2. **During clarifying questions** — Use `find_implementations`, `get_type_hierarchy`, and `find_callers` to ground questions in actual architecture rather than assumptions.

3. **When proposing approaches** — Call `get_di_registrations` for current DI wiring, `find_reflection_usage` for hidden coupling, `get_type_hierarchy` for extension points.

4. **During design presentation** — Reference concrete types, interfaces, and call sites. Example: "These 3 classes implement IUserService: UserService, CachedUserService, AdminUserService."

## During Refactor Analysis

When analyzing refactors in a .NET codebase:

- **Direct Dependency Mapping:** Use `find_callers` + `find_implementations` instead of Grep for accurate dependency tracking.
- **Transitive Closure:** Use `get_type_hierarchy` + `get_project_dependencies` for semantic traversal instead of text search.
- **Risk Identification:** Use `find_reflection_usage` to detect dynamic/hidden coupling that text search misses.

## Tool Quick Reference

| Tool | When to Use |
|------|-------------|
| `find_implementations` | "What implements this interface?" / "What extends this class?" |
| `find_callers` | "Who calls this method?" / "What depends on this?" |
| `get_type_hierarchy` | "What's the inheritance chain?" / "What are the extension points?" |
| `get_di_registrations` | "How is this wired up?" / "What's the DI lifetime?" |
| `get_project_dependencies` | "How do projects relate?" / "What's the dependency graph?" |
| `get_symbol_context` | "Give me everything about this type" (one-shot) |
| `find_reflection_usage` | "Is this used dynamically?" / "Are there hidden dependencies?" |
