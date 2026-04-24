---
title: External Assemblies
---

# External Assemblies

`roslyn-codelens-mcp` can analyse assemblies referenced by your solution — including closed-source NuGet packages.

## How origin tracking works

Every resolved symbol carries a `SymbolOrigin` that classifies where it comes from:

| Kind | Meaning |
|------|---------|
| `source` | Defined in a `.cs` file in the loaded solution |
| `metadata` | From a referenced compiled DLL |

## Tier 1: Cross-assembly navigation

`find_references`, `find_callers`, and `find_implementations` all accept metadata symbol names. Calling `find_callers` for `ILogger.LogInformation` returns all places in your source that call it — even though `ILogger` lives in a NuGet package.

Resolution uses `GetTypeByMetadataName` — the same Roslyn API that Visual Studio uses for "Go to Definition" on external types.

## Tier 2: Assembly inspection

`inspect_external_assembly` lets you browse the public API surface of any referenced assembly:

- **`mode=summary`** — namespace tree with type counts. Use this to discover what's available.
- **`mode=namespace`** — full type listings with members for a given namespace.

To find available assembly names, call `get_nuget_dependencies`.

## Tier 3: IL disassembly

`peek_il` disassembles a specific method to MSIL using [ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy) (MIT license). Useful for understanding non-obvious behavior in third-party code.

The PEFile cache is invalidated automatically when a DLL changes on disk.

## Which assembly name to use?

Use the NuGet package name without the version, e.g. `Newtonsoft.Json`. To see all available names:

```
Use get_nuget_dependencies
```
