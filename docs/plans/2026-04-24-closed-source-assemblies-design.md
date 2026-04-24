# Closed-Source Assembly Analysis — Design

> Addresses [#95](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/95). Brainstormed and approved 2026-04-24.

## Goal

Enable semantic analysis of assemblies for which source is not available — third-party NuGet packages and internal company binaries referenced by the active solution. Developers should be able to inspect API surface, read XML docs, find where their code uses external symbols, and when needed peek the IL of a specific method.

## Scope

**In scope**

- API surface of metadata symbols: types, members, signatures, attributes, inheritance, generic parameters.
- XML documentation loaded from sidecar `.xml` files.
- Source-to-metadata reference queries (who in my code calls this external method, implements this external interface).
- Raw IL disassembly for individual methods in closed-source assemblies.

**Out of scope (explicit non-goals)**

- No C# decompilation. IL only.
- No arbitrary DLL paths. Analysis is scoped to assemblies referenced by the active solution. The skill teaches a "add a temp `ProjectReference`, reload" workaround for the rare case where browsing a not-yet-referenced DLL is needed.
- No NuGet-cache browsing without a solution loaded.
- No extension of method-body or project-scoped analyses (Tier 3 below).
- No metadata symbol-name index — resolution uses Roslyn's built-in lookups.
- `peek_il` does not return structured instruction arrays — only ilasm text.

## Tool surface

### New tools

#### `inspect_external_assembly`

| Field | Value |
|---|---|
| Input | `assemblyName: string`, `mode: "summary" \| "namespace" = "summary"`, `namespace?: string` (required when `mode === "namespace"`) |
| Summary output | `{ name, version, targetFramework, publicKeyToken, namespaceTree: [{ namespace, typeCount, publicTypeNames: string[] }] }` — no members, no docs, bounded regardless of DLL size |
| Namespace output | For each public type in the namespace: kind, signature, modifiers, base/interfaces, members with signatures and XML-doc summaries, attributes. No IL. |
| Errors | Assembly not referenced by any project in the active solution (hints at `get_nuget_dependencies`). Unknown namespace. |

Chosen shape (summary + namespace drill-down) handles size variance from ~30 types (`Microsoft.Extensions.DependencyInjection.Abstractions`) to ~5000 types (`System.Private.CoreLib`) without blowing token budget. Mirrors how `get_file_overview` → `get_type_overview` compose in this project.

#### `peek_il`

| Field | Value |
|---|---|
| Input | `methodSymbol: string` — fully qualified with parameter types, e.g. `"Newtonsoft.Json.JsonConvert.SerializeObject(object, Newtonsoft.Json.Formatting)"` |
| Output | ilasm-style text: method signature, `.locals init (...)`, labeled IL instructions, `.try`/`handler` regions. Produced by `ICSharpCode.Decompiler.Disassembler.ReflectionDisassembler`. |
| Errors | Symbol resolves to source (directs caller to `go_to_definition`). Symbol resolves to abstract/interface member or property with no body. Symbol not found. |

Method-only input avoids accidental context blowup from dumping every overload. ilasm text is the format LLMs recognize as IL from training data — structured JSON would be a novelty.

### Extended existing tools

All extended tools gain an origin block in their output when the resolved symbol is metadata:

```
origin: "metadata"
assemblyName: "Newtonsoft.Json"
assemblyVersion: "13.0.3"
docId: "M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object)"
```

`sourceLocation` is omitted rather than faked.

**Tier 1 — semantic queries on metadata symbols.** Roslyn's underlying calls already return `ISymbol` instances for metadata; tools just drop their source-only guard and add the origin block.

- `get_symbol_context`
- `get_type_overview`
- `get_type_hierarchy` (derived types remain source-scanned — we cannot enumerate derivations across the whole ecosystem)
- `search_symbols`
- `find_attribute_usages`
- `go_to_definition`

**Tier 2 — source-to-metadata references.** Uses `SymbolFinder.FindReferencesAsync` / `SymbolFinder.FindImplementationsAsync`, which operate on metadata symbols natively. Highest-value piece of the feature: "who in my code depends on this external method/interface."

- `find_references`
- `find_callers`
- `find_implementations`

**Tier 3 — explicitly not extended.** Either require method bodies we do not have, or are project-scoped analyses that would produce noise when fanned out across transitive dependencies.

- `analyze_data_flow`, `analyze_control_flow`
- `analyze_change_impact`
- `get_code_actions`, `apply_code_action`, `get_code_fixes`
- `find_unused_symbols`, `find_large_classes`, `find_naming_violations`, `get_complexity_metrics`, `find_reflection_usage`, `get_di_registrations`

## Architecture

### Symbol resolution layer

New component: `MetadataSymbolResolver`. Given a symbol name string, returns `ISymbol + origin`:

1. Query the existing source `SymbolIndex`. If it resolves, return `origin: "source"`.
2. Otherwise call `Compilation.GetTypeByMetadataName(name)` on the active solution's compilations. For member lookups, parse `TypeName.MemberName(paramTypes)` and call `ITypeSymbol.GetMembers()` filtered by signature.
3. If nothing matches, error.

Ambiguity (same name in source and metadata) resolves to source — matching how the compilation itself binds. No separate metadata index is built.

### Assembly discovery

`Compilation.GetReferencedAssemblySymbols()` already contains every direct and transitive assembly the compilation binds against. MSBuild flattens package references so we do not walk `IAssemblySymbol.Modules[0].ReferencedAssemblySymbols` ourselves. When multiple projects reference the same assembly at different versions, symbols are unioned across projects.

### IL peek pipeline

`IMethodSymbol` carries `ContainingAssembly.MetadataName` and a `DocumentationCommentId`. From the matching `PortableExecutableReference` we get the DLL path → open `PEReader` via `System.Reflection.Metadata` → hand to `ICSharpCode.Decompiler.Disassembler.ReflectionDisassembler` keyed by the method's metadata token. Output is ilasm text.

### XML docs

Roslyn loads XML docs via a `DocumentationProvider` attached to a `PortableExecutableReference`. For NuGet-restored assemblies the `.xml` sidecar sits next to the DLL and `MSBuildWorkspace` wires `XmlDocumentationProvider.CreateFromFile` automatically. Audit item: verify this actually happens in the current metadata-reference construction path; attach a provider explicitly if missing. We consume docs via `ISymbol.GetDocumentationCommentXml()` — no new format parsing.

### Caching and hot reload

Two caches, both keyed by `(assemblyPath, fileTimestamp)`:

- `AssemblySummaryCache` — `inspect_external_assembly summary` results (namespace tree + type counts per namespace).
- `PEFileCache` — open `PEFile` and `ReflectionDisassembler` instances for repeated `peek_il` calls on the same DLL.

Both hook `FileChangeTracker`. Source-file changes leave both caches untouched; referenced-assembly file changes (NuGet restore, rebuild of an internal dep) invalidate the matching entries.

### New components inventory

- `src/RoslynCodeLens/Symbols/MetadataSymbolResolver.cs`
- `src/RoslynCodeLens/Tools/InspectExternalAssemblyTool.cs` + `Logic/InspectExternalAssemblyLogic.cs`
- `src/RoslynCodeLens/Tools/PeekIlTool.cs` + `Logic/PeekIlLogic.cs`
- `src/RoslynCodeLens/Metadata/AssemblySummaryCache.cs`
- `src/RoslynCodeLens/Metadata/PEFileCache.cs`
- `src/RoslynCodeLens/Metadata/IlDisassemblerAdapter.cs` — thin wrapper so tool code never references `ICSharpCode.Decompiler` types directly.

Changes to existing files are small — each extended tool drops its source-only guard and adds the origin block to its output DTO. Most of the diff in extended-tool work lives in tests.

### New dependency

`ICSharpCode.Decompiler` NuGet package (LGPL-2.1). Only the disassembler (`Disassembler.ReflectionDisassembler`) is used — no decompiler types are referenced. Attribution added to `NOTICE` and `README`.

## SKILL.md updates

First-class deliverable. Updates to `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`:

- New **Working with external assemblies** section: what counts as external, how symbols are addressed, the `origin: "metadata"` marker.
- **Per-tool metadata behavior table** — one row per existing tool: *works / does not work / caveats*. The Tier 3 list is surfaced explicitly so Claude does not waste calls on tools that will reject metadata input.
- Documentation for both new tools with worked examples — summary → namespace drill-down for exploration; `find_callers` → `peek_il` for "what does this method actually do."
- **Scope-limitation workarounds**: arbitrary DLL path → add a temp `ProjectReference` / `<Reference>` to a throwaway project, reload. Same pattern for browsing NuGet-cache entries not referenced by any project. These are documented SKILL patterns, not server features.
- Decision tree at the top: *symbol in my source → existing tools. External symbol with a name → same existing tools (Tier 1+2). Explore a package → `inspect_external_assembly`. See what a method actually does → `peek_il`.*

## Testing

xUnit pattern per existing code:

- `MetadataSymbolResolverTests` — fixture referencing a small known public NuGet. Covers type, member, overload ambiguity, source-shadows-metadata, not-found.
- `InspectExternalAssemblyLogicTests` — summary shape, namespace mode, error on unreferenced assembly, error on unknown namespace.
- `PeekIlLogicTests` — known-method IL output contains expected opcodes, error on abstract/interface member, error on source symbol.
- Extended-tool tests — one test per extended tool asserting metadata input yields the expected origin block, and for Tier 2 that source-to-metadata references are discovered. Most can extend existing fixtures by targeting a NuGet symbol instead of a source one.
- Benchmarks — `inspect_external_assembly` (summary + namespace) and `peek_il` added to the BenchmarkDotNet suite. `MetadataSymbolResolver` gets its own micro-benchmark to guard against naive-enumeration regressions.

## Phased rollout

Three independently shippable phases, each a PR:

1. **Phase 1 — foundations + Tier 1.** `MetadataSymbolResolver`, origin-field plumbing on output DTOs, the six Tier 1 extended tools, `inspect_external_assembly`, SKILL.md updates for Phase 1 scope, `AssemblySummaryCache`. No new library dependency yet.
2. **Phase 2 — Tier 2 references.** `find_references` / `find_callers` / `find_implementations` accept metadata input. Small phase — mostly guard removal + tests — but isolated because it delivers the highest-value user-facing capability and deserves its own review.
3. **Phase 3 — `peek_il`.** Adds the `ICSharpCode.Decompiler` dependency (attribution, NOTICE update), `PeekIlTool`, `PEFileCache`, `IlDisassemblerAdapter`, SKILL.md updates, benchmarks. Isolated so the dependency addition can be reverted cleanly if license or binary-size concerns surface.

## Open questions for implementation

- Verify `XmlDocumentationProvider` is attached to `PortableExecutableReference` in the current MSBuildWorkspace-loading path. If not, wire it up in Phase 1.
- Confirm `Compilation.GetTypeByMetadataName` behavior for generic types in our Roslyn 4.14 — should accept both `List\`1` metadata-name form and `List<T>` display form consistently. Document the accepted input form in the tool docs and in SKILL.md.
- Decide whether `inspect_external_assembly summary` should sort namespaces alphabetically or by type count — pick one for determinism in Phase 1, document.
