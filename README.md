# Roslyn CodeLens MCP Server

[![NuGet](https://img.shields.io/nuget/v/RoslynCodeLens.Mcp?style=flat-square&logo=nuget&color=blue)](https://www.nuget.org/packages/RoslynCodeLens.Mcp)
[![NuGet Downloads](https://img.shields.io/nuget/dt/RoslynCodeLens.Mcp?style=flat-square&color=green)](https://www.nuget.org/packages/RoslynCodeLens.Mcp)
[![Build Status](https://img.shields.io/github/actions/workflow/status/MarcelRoozekrans/roslyn-codelens-mcp/ci.yml?branch=main&style=flat-square&logo=github)](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/actions)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/roslyn-codelens-mcp?style=flat-square)](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/blob/main/LICENSE)
[![Docs](https://img.shields.io/badge/docs-GitHub%20Pages-blue?style=flat-square)](https://marcelroozekrans.github.io/roslyn-codelens-mcp/)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?style=flat&logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

A Roslyn-based MCP server that gives AI agents deep semantic understanding of .NET codebases — type hierarchies, call graphs, DI registrations, diagnostics, refactoring, and more.

<a href="https://glama.ai/mcp/servers/MarcelRoozekrans/roslyn-codelens-mcp">
  <img width="380" height="200" src="https://glama.ai/mcp/servers/MarcelRoozekrans/roslyn-codelens-mcp/badge" alt="roslyn-codelens-mcp MCP server" />
</a>

<!-- mcp-name: io.github.MarcelRoozekrans/roslyn-codelens -->

---

## Hosted deployment

A hosted deployment is available on [Fronteir AI](https://fronteir.ai/mcp/marcelroozekrans-roslyn-codelens-mcp).

## Features

- **find_implementations** — Find all classes/structs implementing an interface or extending a class
- **find_callers** — Find every call site for a method, property, or constructor
- **find_tests_for_symbol** — List xUnit/NUnit/MSTest methods that exercise a production symbol; opt-in transitive walk through helpers
- **find_uncovered_symbols** — Public methods and properties no test transitively reaches; sorted by cyclomatic complexity for prioritization
- **get_type_hierarchy** — Walk base classes, interfaces, and derived types
- **get_di_registrations** — Scan for DI service registrations
- **get_project_dependencies** — Get the project reference graph
- **get_symbol_context** — One-shot context dump for any type
- **get_public_api_surface** — Enumerate every public/protected type and member in production projects; flat, deterministically-sorted list suitable for API review or breaking-change baselines.
- **find_reflection_usage** — Detect dynamic/reflection-based usage
- **find_references** — Find all references to any symbol (types, methods, properties, fields, events)
- **go_to_definition** — Find the source file and line where a symbol is defined
- **get_diagnostics** — List compiler errors, warnings, and Roslyn analyzer diagnostics
- **get_code_fixes** — Get available code fixes with structured text edits for any diagnostic
- **search_symbols** — Fuzzy workspace symbol search by name
- **get_nuget_dependencies** — List NuGet package references per project
- **find_attribute_usages** — Find types and members decorated with a specific attribute
- **find_circular_dependencies** — Detect cycles in project or namespace dependency graphs
- **get_complexity_metrics** — Cyclomatic complexity analysis per method
- **find_naming_violations** — Check .NET naming convention compliance
- **find_async_violations** — Sync-over-async, `async void` misuse, missing awaits, fire-and-forget tasks; per-violation report with severity
- **find_disposable_misuse** — `IDisposable`/`IAsyncDisposable` instances not wrapped in `using`/`await using`/returned/assigned to field; severity error/warning per violation.
- **find_large_classes** — Find oversized types by member or line count
- **find_unused_symbols** — Dead code detection via reference analysis
- **get_source_generators** — List source generators and their output per project
- **get_generated_code** — Inspect generated source code from source generators
- **inspect_external_assembly** — Browse types, members, and XML docs from closed-source NuGet packages and referenced assemblies
- **peek_il** — Decompile any method to ilasm-style IL bytecode from closed-source or generated assemblies
- **get_code_actions** — Discover available refactorings and fixes at any position (extract method, rename, inline variable, and more)
- **apply_code_action** — Execute any Roslyn refactoring by title, with preview mode (returns a diff before writing to disk)
- **list_solutions** — List all loaded solutions and which one is currently active
- **set_active_solution** — Switch the active solution by partial name (all subsequent tools operate on it)
- **load_solution** — Load an additional .sln/.slnx at runtime and make it the active solution
- **unload_solution** — Unload a loaded solution to free memory
- **rebuild_solution** — Force a full reload of the analyzed solution
- **analyze_data_flow** — Variable read/write/capture analysis within a statement range (declared, read, written, always assigned, captured, flows in/out)
- **analyze_control_flow** — Branch/loop reachability analysis within a statement range (start/end reachability, return statements, exit points)
- **analyze_change_impact** — Show all files, projects, and call sites affected by changing a symbol — combines find_references and find_callers
- **get_type_overview** — Compound tool: type context + hierarchy + file diagnostics in one call
- **analyze_method** — Compound tool: method signature + callers + outgoing calls in one call
- **get_file_overview** — Compound tool: types defined in a file + file-scoped diagnostics in one call

## External Assemblies

Metadata-origin symbols (from NuGet packages and referenced assemblies) are first-class citizens:

- **Tier 1 — Navigation** (`find_references`, `find_callers`, `find_implementations`): Accepts closed-source type and member names. Resolves them from assembly metadata and reports all source-level usage sites.
- **Tier 2 — Inspection** (`inspect_external_assembly`): Browse namespaces, types, members, and XML doc comments from any referenced assembly without decompiling.
- **Tier 3 — IL** (`peek_il`): Decompile a specific method to annotated ilasm-style IL using ICSharpCode.Decompiler — useful for understanding the internals of NuGet libraries.

Location-returning results include an `Origin` field (`source` or `metadata`) and an `IsGenerated` flag to distinguish hand-written code from closed-source or generated output.

## Quick Start

### VS Code / Visual Studio (via dnx)

Add to your MCP settings (`.vscode/mcp.json` or VS settings):

```json
{
  "servers": {
    "roslyn-codelens": {
      "type": "stdio",
      "command": "dnx",
      "args": ["RoslynCodeLens.Mcp", "--yes"]
    }
  }
}
```

### Claude Code Plugin

```bash
claude install gh:MarcelRoozekrans/roslyn-codelens-mcp
```

### .NET Global Tool

```bash
dotnet tool install -g RoslynCodeLens.Mcp
```

Then add to your MCP client config:

```json
{
  "mcpServers": {
    "roslyn-codelens": {
      "command": "roslyn-codelens-mcp",
      "args": [],
      "transport": "stdio"
    }
  }
}
```

## Usage

The server automatically discovers `.sln` files by walking up from the current directory. You can also pass one or more solution paths directly:

```bash
# Single solution
roslyn-codelens-mcp /path/to/MySolution.sln

# Multiple solutions — switch between them with set_active_solution
roslyn-codelens-mcp /path/to/A.sln /path/to/B.sln
```

When multiple solutions are loaded, use `list_solutions` to see what's available and `set_active_solution("B")` to switch context. The first path is active by default.

## Performance

All type lookups use pre-built reverse inheritance maps, member indexes, and attribute indexes for O(1) access. Benchmarked on an i9-12900HK with .NET 10.0.7:

| Tool | Latency | Memory |
|------|--------:|-------:|
| `go_to_definition` | 1.5 µs | 576 B |
| `get_project_dependencies` | 1.9 µs | 1.5 KB |
| `find_implementations` | 2.8 µs | 720 B |
| `get_symbol_context` | 2.9 µs | 1.0 KB |
| `find_circular_dependencies` | 3.3 µs | 2.4 KB |
| `get_type_hierarchy` | 4.2 µs | 1.3 KB |
| `get_source_generators` | 14 µs | 17 KB |
| `analyze_data_flow` | 17 µs | 1.2 KB |
| `inspect_external_assembly` (summary) | 57 µs | 22 KB |
| `get_generated_code` | 60 µs | 18 KB |
| `find_attribute_usages` | 99 µs | 904 B |
| `analyze_control_flow` | 180 µs | 14 KB |
| `find_large_classes` | 303 µs | 1.4 KB |
| `get_di_registrations` | 473 µs | 14 KB |
| `get_complexity_metrics` | 556 µs | 12 KB |
| `find_reflection_usage` | 588 µs | 17 KB |
| `get_type_overview` | 607 µs | 54 KB |
| `get_diagnostics` | 643 µs | 50 KB |
| `get_nuget_dependencies` | 787 µs | 37 KB |
| `get_file_overview` | 799 µs | 53 KB |
| `inspect_external_assembly` (namespace) | 1.1 ms | 270 KB |
| `peek_il` | 1.1 ms | 33 KB |
| `get_code_actions` | 1.2 ms | 53 KB |
| `find_callers` | 4.3 ms | 160 KB |
| `search_symbols` | 4.4 ms | 552 KB |
| `find_tests_for_symbol` (direct) | 4.4 ms | 203 KB |
| `analyze_method` | 4.4 ms | 160 KB |
| `find_uncovered_symbols` | 6.6 ms | 180 KB |
| `find_tests_for_symbol` (transitive) | 8.0 ms | 356 KB |
| `find_references` | 13 ms | 606 KB |
| `find_unused_symbols` | 19 ms | 617 KB |
| `find_naming_violations` | 21 ms | 785 KB |
| `analyze_change_impact` | 21 ms | 773 KB |
| Solution loading (one-time) | ~4.7 s | 13 MB |

## Hot Reload

The server watches `.cs`, `.csproj`, `.props`, and `.targets` files for changes. When a change is detected, affected projects are lazily re-compiled on the next tool query — only stale projects and their downstream dependents are re-compiled, not the full solution.

Location-returning tools include an `IsGenerated` flag to distinguish source-generator output from hand-written code.

## Requirements

- .NET 10 SDK
- A .NET solution with compilable projects

## Development

```bash
dotnet build
dotnet test
dotnet run --project benchmarks/RoslynCodeLens.Benchmarks -c Release
```

## Third-party licenses

- [ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy) — MIT license (v8+). Used for IL disassembly in the `peek_il` tool.

## License

MIT