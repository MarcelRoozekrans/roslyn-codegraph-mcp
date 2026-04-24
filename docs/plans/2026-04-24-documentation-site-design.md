# Documentation Site Design

**Date:** 2026-04-24  
**Goal:** A Docusaurus documentation site for users of `roslyn-codelens-mcp`, deployed to GitHub Pages, with auto-generated tool reference pages and hand-written guides.

---

## Audience

Developers using `roslyn-codelens-mcp` as a tool — integrating it into their Claude Code workflow, configuring it, and understanding what each of the 36 tools does.

---

## Architecture

### Site generator

**Docusaurus** (React-based, versioned, Markdown-first). Project lives at `docs/site/` inside the existing repo.

### Hosting

**GitHub Pages** at `https://marcelroozekrans.github.io/roslyn-codelens-mcp/`.

### Repo layout

```
docs/
  site/                          ← Docusaurus project root
    docusaurus.config.ts
    sidebars.ts
    static/
    src/css/custom.css
    docs/
      index.md                   ← Landing page
      getting-started/
        installation.md          ← Manual .mcp.json setup
        marketplace.md           ← Install via Claude marketplace
        configuration.md
        first-use.md
      guides/
        analyze-a-codebase.md
        find-external-usages.md
        inspect-nuget-packages.md
        refactor-with-code-actions.md
        understand-di-wiring.md
      tools/                     ← AUTO-GENERATED (not committed)
        find-references.md
        find-callers.md
        ...  (36 files, one per tool)
      external-assemblies.md     ← Conceptual: origin.kind="metadata"
      faq.md
  tool-extras/                   ← Hand-written enrichment for auto-gen pages
    find-references.extra.md
    inspect-external-assembly.extra.md
    peek-il.extra.md
    ...
tools/
  DocGen/                        ← .NET console project: generates tools/*.md
    DocGen.csproj
    Program.cs
```

---

## Auto-generation

A .NET console project (`tools/DocGen/`) reflects over the `RoslynCodeLens` assembly at build time:

1. Enumerates all classes with `[McpServerToolType]`
2. Reads `[McpServerTool(Name = "...")]` and `[Description("...")]` on the method and its parameters
3. Emits one `.md` file per tool with Docusaurus frontmatter + parameter table

Generated page structure:

```md
---
title: find_references
sidebar_label: find_references
description: <from [Description] attribute>
slug: /tools/find-references
---

# find_references

<tool description>

## Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `symbol`  | string | ... |

## Returns

<return type description>

<!-- merged from docs/tool-extras/find-references.extra.md if present -->
```

**Sidecar `.extra.md` files** in `docs/tool-extras/` are merged at generation time. They contain hand-written sections: extended examples, caveats, "see also" links. If no sidecar exists, the page is valid without it.

**Generated files are not committed.** They are produced fresh in CI and discarded after the site is built.

---

## CI / Deployment

### `docs-build.yml` — runs on every PR

```
1. dotnet build tools/DocGen
2. dotnet run --project tools/DocGen -- --output docs/site/docs/tools/
3. cd docs/site && npm ci && npm run build
```

Fails the PR if the Docusaurus build breaks. Ensures tool description changes don't silently break the site.

### `docs-deploy.yml` — runs on push to `main`

Same steps as above, then deploys the `build/` output to GitHub Pages via `actions/deploy-pages`.

---

## Content plan

### Hand-written pages (~11)

| Page | Purpose |
|------|---------|
| `index.md` | Landing — what it is, why symbol-name API beats coordinates |
| `getting-started/installation.md` | Prerequisites, `.mcp.json` setup, verify server starts |
| `getting-started/marketplace.md` | Install via Claude marketplace / superpowers extensions |
| `getting-started/configuration.md` | Solution path, multi-solution, environment variables |
| `getting-started/first-use.md` | Call `get_type_overview`, read the result |
| `guides/analyze-a-codebase.md` | Onboarding: project deps → type overview → DI → diagnostics |
| `guides/find-external-usages.md` | Trace NuGet package usage with `find_references` / `find_callers` |
| `guides/inspect-nuget-packages.md` | Browse closed-source assemblies: `inspect_external_assembly` + `peek_il` |
| `guides/refactor-with-code-actions.md` | `get_code_actions` → `apply_code_action` with preview |
| `guides/understand-di-wiring.md` | `get_di_registrations` → `find_implementations` → `analyze_change_impact` |
| `external-assemblies.md` | Conceptual: metadata origin, Tier-1 vs inspect vs peek_il |
| `faq.md` | Why not grep, multi-solution, performance, hot-reload |

### Auto-generated pages (36)

One page per tool, grouped in sidebar by category:

| Sidebar category | Tools |
|-----------------|-------|
| Navigation | `go_to_definition`, `search_symbols`, `find_references`, `find_callers`, `find_implementations`, `find_attribute_usages` |
| Analysis | `get_symbol_context`, `get_type_overview`, `get_type_hierarchy`, `get_file_overview`, `analyze_method`, `analyze_change_impact`, `analyze_data_flow`, `analyze_control_flow` |
| Diagnostics & Refactoring | `get_diagnostics`, `get_code_fixes`, `get_code_actions`, `apply_code_action` |
| Code Quality | `find_unused_symbols`, `get_complexity_metrics`, `find_naming_violations`, `find_large_classes`, `find_circular_dependencies`, `find_reflection_usage` |
| DI & Dependencies | `get_di_registrations`, `get_nuget_dependencies`, `get_project_dependencies` |
| Source Generators | `get_source_generators`, `get_generated_code` |
| External Assemblies | `inspect_external_assembly`, `peek_il` |
| Solution Management | `list_solutions`, `set_active_solution`, `load_solution`, `unload_solution`, `rebuild_solution` |

---

## NuGet package integration

Once the docs site is deployed, update `<PackageProjectUrl>` in `src/RoslynCodeLens/RoslynCodeLens.csproj` to point to the GitHub Pages URL:

```xml
<PackageProjectUrl>https://marcelroozekrans.github.io/roslyn-codelens-mcp/</PackageProjectUrl>
```

`<RepositoryUrl>` stays pointing to the GitHub source repo. NuGet.org shows `PackageProjectUrl` as the "Project URL" on the package page — this makes the docs site the first thing users see when they find the package.

---

## Key decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Site generator | Docusaurus | React-based, versioned, excellent search, widely used for dev tools |
| Hosting | GitHub Pages | Free, automatic, no infra needed |
| Tool reference | Reflection-based auto-gen | Always in sync with code; no manual upkeep |
| Generated files | Not committed | Avoids noisy diffs; CI produces them fresh |
| Enrichment | Sidecar `.extra.md` | Separates structured data (code) from prose (docs) |
| Marketplace page | Yes, in Getting Started | Installation via superpowers extensions is a first-class path |
| NuGet project URL | GitHub Pages docs URL | Docs site is more useful to package consumers than the raw source repo |
