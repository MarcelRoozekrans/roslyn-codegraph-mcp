# Roslyn Code Graph MCP Server

A Roslyn-based MCP server that provides semantic code intelligence for .NET codebases. Designed for use with Claude Code to understand type hierarchies, call sites, DI registrations, and reflection usage.

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
- **get_diagnostics** — List compiler errors and warnings across the solution
- **search_symbols** — Fuzzy workspace symbol search by name

## Installation

### As a Claude Code Plugin

```bash
claude install gh:MarcelRoozekrans/roslyn-codegraph-mcp
```

### As a .NET Global Tool

```bash
dotnet tool install -g RoslynCodeGraph.Mcp
```

### Manual MCP Configuration

Add to your Claude Code MCP settings:

```json
{
  "mcpServers": {
    "roslyn-codegraph": {
      "command": "roslyn-codegraph-mcp",
      "args": [],
      "transport": "stdio"
    }
  }
}
```

## Usage

The server automatically discovers `.sln` files by walking up from the current directory. You can also pass a solution path directly:

```bash
roslyn-codegraph-mcp /path/to/MySolution.sln
```

## Performance

All type lookups use pre-built reverse inheritance maps and member indexes for O(1) access. Benchmarked on an i9-12900HK with .NET 10.0.3:

| Tool | Latency | Memory |
|------|--------:|-------:|
| `get_project_dependencies` | 311 ns | 1.2 KB |
| `go_to_definition` | 363 ns | 528 B |
| `find_implementations` | 684 ns | 624 B |
| `get_type_hierarchy` | 749 ns | 816 B |
| `get_symbol_context` | 1.2 µs | 1.0 KB |
| `get_diagnostics` | 28 µs | 22 KB |
| `get_di_registrations` | 59 µs | 13 KB |
| `find_reflection_usage` | 82 µs | 15 KB |
| `find_callers` | 171 µs | 32 KB |
| `search_symbols` | 543 µs | 2.1 KB |
| `find_references` | 911 µs | 187 KB |
| Solution loading (one-time) | ~929 ms | 8 MB |

## Requirements

- .NET 10 SDK
- A .NET solution with compilable projects

## Development

```bash
dotnet build
dotnet test
dotnet run --project benchmarks/RoslynCodeGraph.Benchmarks -c Release
```

## License

MIT
