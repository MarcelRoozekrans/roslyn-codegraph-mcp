# Roslyn CodeLens MCP Server

[![NuGet](https://img.shields.io/nuget/v/RoslynCodeLens.Mcp?style=flat-square&logo=nuget&color=blue)](https://www.nuget.org/packages/RoslynCodeLens.Mcp)
[![NuGet Downloads](https://img.shields.io/nuget/dt/RoslynCodeLens.Mcp?style=flat-square&color=green)](https://www.nuget.org/packages/RoslynCodeLens.Mcp)
[![Build Status](https://img.shields.io/github/actions/workflow/status/MarcelRoozekrans/roslyn-codelens-mcp/ci.yml?branch=main&style=flat-square&logo=github)](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/actions)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/roslyn-codelens-mcp?style=flat-square)](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/blob/main/LICENSE)

A Roslyn-based MCP server that gives AI agents deep semantic understanding of .NET codebases — type hierarchies, call graphs, DI registrations, diagnostics, refactoring, and more.

<a href="https://glama.ai/mcp/servers/MarcelRoozekrans/roslyn-codelens-mcp">
  <img width="380" height="200" src="https://glama.ai/mcp/servers/MarcelRoozekrans/roslyn-codelens-mcp/badge" alt="roslyn-codelens-mcp MCP server" />
</a>

<!-- mcp-name: io.github.marcelroozekrans/roslyn-codelens -->

---

## Features

- **find_implementations** — Find all classes/structs implementing an interface or extending a class
- **find_callers** — Find every call site for a method, property, or constructor
- **get_type_hierarchy** — Walk base classes, interfaces, and derived types
- **get_di_registrations** — Scan for DI service registrations
- **get_project_dependencies** — Get the project reference graph
- **get_symbol_context** — One-shot context dump for any type
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
- **find_large_classes** — Find oversized types by member or line count
- **find_unused_symbols** — Dead code detection via reference analysis
- **get_source_generators** — List source generators and their output per project
- **get_generated_code** — Inspect generated source code from source generators
- **get_code_actions** — Discover available refactorings and fixes at any position (extract method, rename, inline variable, and more)
- **apply_code_action** — Execute any Roslyn refactoring by title, with preview mode (returns a diff before writing to disk)
- **rebuild_solution** — Force a full reload of the analyzed solution
- **analyze_data_flow** — Variable read/write/capture analysis within a statement range (declared, read, written, always assigned, captured, flows in/out)
- **analyze_control_flow** — Branch/loop reachability analysis within a statement range (start/end reachability, return statements, exit points)
- **analyze_change_impact** — Show all files, projects, and call sites affected by changing a symbol — combines find_references and find_callers
- **get_type_overview** — Compound tool: type context + hierarchy + file diagnostics in one call
- **analyze_method** — Compound tool: method signature + callers + outgoing calls in one call
- **get_file_overview** — Compound tool: types defined in a file + file-scoped diagnostics in one call

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

All type lookups use pre-built reverse inheritance maps, member indexes, and attribute indexes for O(1) access. Benchmarked on an i9-12900HK with .NET 10.0.4:

| Tool | Latency | Memory |
|------|--------:|-------:|
| `find_circular_dependencies` | 892 ns | 1.2 KB |
| `get_project_dependencies` | 987 ns | 1.3 KB |
| `go_to_definition` | 1.3 µs | 608 B |
| `get_type_hierarchy` | 1.8 µs | 856 B |
| `find_implementations` | 2.0 µs | 704 B |
| `get_symbol_context` | 3.0 µs | 960 B |
| `get_source_generators` | 4.9 µs | 7.1 KB |
| `analyze_data_flow` | 7.4 µs | 880 B |
| `find_attribute_usages` | 20 µs | 312 B |
| `get_generated_code` | 30 µs | 7.8 KB |
| `analyze_control_flow` | 96 µs | 13 KB |
| `get_diagnostics` | 98 µs | 22 KB |
| `get_complexity_metrics` | 127 µs | 5.6 KB |
| `get_type_overview` | 138 µs | 24 KB |
| `find_large_classes` | 148 µs | 888 B |
| `get_file_overview` | 162 µs | 24 KB |
| `get_di_registrations` | 172 µs | 13 KB |
| `get_nuget_dependencies` | 202 µs | 15 KB |
| `find_reflection_usage` | 235 µs | 15 KB |
| `find_callers` | 406 µs | 37 KB |
| `analyze_method` | 619 µs | 38 KB |
| `get_code_actions` | 765 µs | 49 KB |
| `search_symbols` | 1.3 ms | 2.5 KB |
| `find_unused_symbols` | 3.8 ms | 207 KB |
| `find_references` | 3.9 ms | 204 KB |
| `analyze_change_impact` | 4.2 ms | 243 KB |
| `find_naming_violations` | 10.7 ms | 654 KB |
| Solution loading (one-time) | ~3.0 s | 8.2 MB |

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

## License

MIT