# Feature Analysis & Adoption Roadmap

> Competitive analysis against a reference Roslyn MCP server implementation (58 tools).

---

## 1. Project Comparison Summary

| Dimension | Reference | This project |
|-----------|-----------|--------------|
| Tools | ~58 | 32 |
| Architecture | Monolithic single file | Modular Tool + Logic split per feature |
| Refactoring | 13 write/mutate tools | `get_code_actions` + `apply_code_action` (generic engine) |
| Code generation | Null checks, equality members | Via code actions |
| Compound/batch | 6 tools | 3 (`get_type_overview`, `analyze_method`, `get_file_overview`) |
| Multi-solution | Env var only | Built-in manager with list/set |
| Roslyn version | 5.0.0 | 4.14.0 |
| .NET target | net8.0 | net10.0 |
| Doc sync | Explicit `sync_documents` tool | FileChangeTracker (automatic) |

---

## 2. Feature Gap Analysis

### 2.1 Tools We Already Have (Parity or Better)

| Their Tool | Our Equivalent | Notes |
|------------|---------------|-------|
| `health_check` | (implicit) | Server status |
| `load_solution` | `list_solutions` / `set_active_solution` | We support multi-solution |
| `get_symbol_info` | `get_symbol_context` | |
| `go_to_definition` | `go_to_definition` | |
| `find_references` | `find_references` | |
| `find_implementations` | `find_implementations` | |
| `find_callers` | `find_callers` | |
| `get_type_hierarchy` | `get_type_hierarchy` | |
| `search_symbols` | `search_symbols` | |
| `get_diagnostics` | `get_diagnostics` | |
| `get_code_fixes` | `get_code_fixes` | |
| `get_complexity_metrics` | `get_complexity_metrics` | |
| `find_unused_code` | `find_unused_symbols` | |
| `get_project_structure` | `get_project_dependencies` | |
| `get_attributes` | `find_attribute_usages` | |
| `get_derived_types` | `get_type_hierarchy` | Included in hierarchy |
| `get_base_types` | `get_type_hierarchy` | Included in hierarchy |
| `get_code_actions_at_position` | `get_code_actions` | **Implemented — Phase 1 complete** |
| `apply_code_action_by_title` | `apply_code_action` | **Implemented — Phase 1 complete** |

### 2.2 Remaining Gaps (Priority Order)

#### Priority 1 — High Value

| Tool | Description | Why Valuable |
|------|-------------|-------------|
| `analyze_change_impact` | Show all locations affected by changing a symbol | Critical for safe refactoring — shows blast radius |
| `analyze_data_flow` | Variable read/write/capture analysis within a method | Helps understand variable lifecycle |
| `analyze_control_flow` | Branch/loop reachability, unreachable code | Helps verify code paths |

#### Priority 2 — Medium Value

| Tool | Description | Why Valuable |
|------|-------------|-------------|
| `validate_code` | Syntax + compilation check without full build | Quick validation after edits |
| `implement_missing_members` | Generate stubs for interface/abstract members | Boilerplate elimination |
| `generate_constructor` | Auto-generate constructors from fields/properties | Boilerplate elimination |
| `get_outgoing_calls` | Methods called by a given method | Inverse of find_callers |

#### Priority 3 — Compound Tools (Token Optimization)

| Tool | Description | Composition |
|------|-------------|-------------|
| `get_type_overview` | Type info + members + hierarchy + diagnostics | Combines 4 existing tools |
| `analyze_method` | Signature + callers + outgoing calls | Combines 2 existing tools |
| `get_file_overview` | File structure + diagnostics | Combines 2 existing tools |
| `get_method_source` | Retrieve method body source code | Targeted source retrieval |

### 2.3 Tools We Have That They Don't

| Our Tool | Description |
|----------|-------------|
| `find_circular_dependencies` | Detect circular project references |
| `find_large_classes` | Find classes exceeding size thresholds |
| `find_naming_violations` | Check .NET naming convention compliance |
| `find_reflection_usage` | Find reflection API usage |
| `get_di_registrations` | Find dependency injection registrations |
| `get_generated_code` | Show source-generator output |
| `get_source_generators` | List active source generators |
| `get_nuget_dependencies` | List NuGet package references |
| `rebuild_solution` | Force full reload |
| `list_solutions` / `set_active_solution` | Multi-solution management |

---

## 3. Implementation Patterns Worth Adopting

### 3.1 Refactoring via Roslyn Code Actions ✅ Done

Load `CodeRefactoringProvider` and `CodeFixProvider` from `Microsoft.CodeAnalysis.CSharp.Features` via reflection. Two generic tools cover all built-in Roslyn refactorings (rename, extract method, inline, encapsulate field, etc.) without implementing each one individually. Preview mode returns a diff before writing.

### 3.2 Compound/Batch Tools

Combine multiple queries into one response to reduce round-trips and token usage:
- `get_type_overview` = type info + members + hierarchy + diagnostics
- `analyze_method` = signature + callers + outgoing calls

### 3.3 Data Flow Analysis

Uses `SemanticModel.AnalyzeDataFlow(node)` — variables declared, read, written, captured by lambdas.

### 3.4 Control Flow Analysis

Uses `SemanticModel.AnalyzeControlFlow(statements)` — entry/exit points, unreachable statements, return/break/continue points.

### 3.5 Change Impact Analysis

Transitive `find_references` + `find_callers` — direct references, affected callers, types, and projects.

---

## 4. Recommended Next Phases

| Phase | What | Status |
|-------|------|--------|
| 1 | Generic refactoring engine (`get_code_actions` + `apply_code_action`) | ✅ Complete |
| 2 | Flow analysis (`analyze_data_flow`, `analyze_control_flow`) | ✅ Complete |
| 3 | Impact analysis (`analyze_change_impact`) | ✅ Complete |
| 4 | Compound tools (`get_type_overview`, `analyze_method`, `get_file_overview`) | ✅ Complete |
| 5 | Code generation (`implement_missing_members`, `generate_constructor`) | N/A — covered by `apply_code_action` (see SKILL.md) |

---

## 5. Architecture Notes

### Patterns to Avoid (from reference implementation)

- Monolithic service file (~5000 lines) — all tool implementations in one place
- No provider caching — reflection runs per-call
- Switch-based routing — brittle, no auto-discovery
- Manual document sync — agents must call `sync_documents` after edits

### Our Advantages to Preserve

- **Modular Tool + Logic split** — each tool independently testable
- **Multi-solution manager** — unique to this project
- **FileChangeTracker** — automatic hot-reload vs manual sync
- **Domain-specific tools** — DI registrations, source generators, NuGet deps, circular deps, naming violations
