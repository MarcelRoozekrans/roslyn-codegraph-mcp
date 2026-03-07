# Roslyn Analyzer + Code Quality Tools Design

**Date:** 2026-03-07
**Status:** Approved

## Overview

Extend the existing Roslyn Code Graph MCP server with two capabilities:

1. **Roslyn Analyzer Integration** — run solution's Roslyn analyzers and surface code fix suggestions
2. **Code Quality Tools** — built-in quality analysis reusing existing `LoadedSolution` and `SymbolResolver`

This brings the server from 13 to 19 tools total.

## Architecture

### Analyzer Integration

Two new services + one new tool + one enhanced tool:

```
┌─────────────────────────────────────────────┐
│  MCP Client (Claude Code)                   │
│                                             │
│  1. get_diagnostics → analyzer warnings     │
│  2. get_code_fixes(id, file, line)          │
│     → text edits for LLM to review/apply    │
└──────────┬──────────────────┬───────────────┘
           │                  │
    ┌──────▼──────┐   ┌──────▼──────┐
    │AnalyzerRunner│   │CodeFixRunner│
    └──────┬──────┘   └──────┬──────┘
           │                  │
    ┌──────▼──────────────────▼──────┐
    │  Roslyn Compilation            │
    │  (already loaded by server)    │
    └────────────────────────────────┘
```

### Technical Approach

Use `Project.AnalyzerReferences` to discover analyzers configured in the solution. No manual `.csproj` parsing required — Roslyn provides resolved analyzer assemblies directly.

## Components

### 1. AnalyzerRunner (new service)

Discovers analyzers from `Project.AnalyzerReferences`, runs them via `CompilationWithAnalyzers`, caches analyzer instances per project.

```csharp
public class AnalyzerRunner
{
    public async Task<ImmutableArray<Diagnostic>> RunAnalyzersAsync(
        Project project,
        Compilation compilation,
        CancellationToken ct);
}
```

- Later: accepts additional `DiagnosticAnalyzer[]` for custom analyzers

### 2. CodeFixRunner (new service)

Discovers `CodeFixProvider` types from analyzer assemblies via `ExportCodeFixProviderAttribute`. Matches providers to diagnostics using `FixableDiagnosticIds`. Invokes provider to get `CodeAction`, extracts `TextChanges` from resulting `Document`.

```csharp
public class CodeFixRunner
{
    public async Task<List<CodeFixSuggestion>> GetFixesAsync(
        Project project,
        Diagnostic diagnostic,
        CancellationToken ct);
}
```

### 3. Enhanced get_diagnostics

Current behavior (compiler diagnostics) + analyzer diagnostics. New optional parameter:

- `includeAnalyzers: bool` (default: true)

Response includes `source` field: `"compiler"` or `"analyzer:<AnalyzerName>"`.

### 4. New get_code_fixes tool

Parameters:
- `diagnosticId: string` (e.g., "CA1822")
- `filePath: string`
- `line: int`

Returns list of fixes, each with title and structured text edits.

## Data Models

```csharp
public record CodeFixSuggestion(
    string Title,
    string DiagnosticId,
    List<TextEdit> Edits);

public record TextEdit(
    string FilePath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string NewText);
```

## Data Flow

1. `get_diagnostics(project: "MyApp", includeAnalyzers: true)` → runs `CompilationWithAnalyzers` → returns diagnostics with IDs, severity, location, source
2. LLM picks a diagnostic (e.g., CA1822 at Program.cs:15)
3. `get_code_fixes(diagnosticId: "CA1822", file: "Program.cs", line: 15)` → finds `CodeFixProvider` → invokes → extracts text changes → returns structured edits
4. LLM reviews edits, applies via Edit tool (or rejects)

## Code Quality Tools

Five new tools reusing existing `LoadedSolution` and `SymbolResolver` infrastructure:

### find_unused_symbols

Dead code detection using `find_references` internally on all public/internal types and members. Reports symbols with zero references outside their own file.

- Parameters: `project` (optional), `includeInternal` (default: false)
- Skips: entry points, test methods, DI registrations, reflection targets

### get_complexity_metrics

Walks `SyntaxTree` counting branches (if/else/switch/for/while/catch/&&/||). Returns cyclomatic complexity per method.

- Parameters: `project` (optional), `threshold` (default: 10)
- Uses: `Compilation.SyntaxTrees`

### find_naming_violations

Checks .NET naming conventions: PascalCase types/methods/properties, camelCase parameters/locals, `I` prefix for interfaces, `_` prefix for private fields.

- Parameters: `project` (optional), `convention` (default: "dotnet", extensible later)
- Uses: `AllTypes` + member enumeration

### find_large_classes

Reports types exceeding thresholds for member count or line count.

- Parameters: `maxMembers` (default: 20), `maxLines` (default: 500), `project` (optional)
- Uses: `AllTypes` + `GetMembers()` + `SyntaxReference` for line spans

### find_circular_dependencies

Detects cycles in project reference graph and namespace dependency graph.

- Parameters: `level` — `"project"` or `"namespace"` (default: "project")
- Uses: existing project dependency graph + namespace graph from `using` directives

## Error Handling

- No analyzers found → return diagnostics with `source: "compiler"` only
- `CodeFixProvider` throws → skip that fix, log warning, return other fixes
- No fixes available → return empty list with message
- Analyzer timeout → cancel after configurable timeout (default 30s), return partial results

## Testing Strategy

- Unit tests with existing test solution
- Add an analyzer NuGet to test project to verify discovery
- Test `get_code_fixes` returns correct text edits for known analyzers
- Test graceful handling of no analyzers / no fixes
- Tests for each code quality tool against test solution

## Extensibility (Future)

Custom analyzer support via configuration parameter:

```
get_diagnostics(customAnalyzerPaths: ["path/to/MyAnalyzer.dll"])
```

Loads additional `DiagnosticAnalyzer` types from specified assemblies and merges with solution analyzers. `AnalyzerRunner` accepts `IEnumerable<DiagnosticAnalyzer>` internally.

## Tool Summary

| Tool | Category | New/Enhanced |
|------|----------|-------------|
| `get_diagnostics` | Analyzer | Enhanced |
| `get_code_fixes` | Analyzer | New |
| `find_unused_symbols` | Quality | New |
| `get_complexity_metrics` | Quality | New |
| `find_naming_violations` | Quality | New |
| `find_large_classes` | Quality | New |
| `find_circular_dependencies` | Quality | New |
