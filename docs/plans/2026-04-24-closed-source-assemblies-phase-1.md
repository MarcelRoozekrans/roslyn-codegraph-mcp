# Phase 1 — Foundations + Tier 1 + inspect_external_assembly

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make Tier-1 read-only tools (`get_symbol_context`, `get_type_overview`, `get_type_hierarchy`, `search_symbols`, `find_attribute_usages`, `go_to_definition`) accept metadata symbols, and add the new `inspect_external_assembly` tool.

**Architecture:** Introduce `MetadataSymbolResolver` as a thin layer that extends `SymbolResolver.FindSymbols` with a metadata fallback via `Compilation.GetTypeByMetadataName`. Extend each Tier-1 `*Logic.cs` to surface metadata symbols in addition to source. New `inspect_external_assembly` tool walks `Compilation.GetReferencedAssemblySymbols()` to produce summary / namespace-mode output.

**Tech Stack:** C# 13 / .NET 10, Roslyn 4.14, xUnit, ModelContextProtocol SDK. No new NuGet dependencies in this phase.

**Fixture:** Existing `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestSolution.slnx` — `TestLib2` already references `Microsoft.Extensions.DependencyInjection`, which supplies stable metadata symbols like `Microsoft.Extensions.DependencyInjection.IServiceCollection` and `ServiceDescriptor`. No fixture changes required.

**Design reference:** [docs/plans/2026-04-24-closed-source-assemblies-design.md](./2026-04-24-closed-source-assemblies-design.md)

---

## Task 1: Add the origin block to SymbolLocation and SymbolContext models

**Files:**
- Modify: [src/RoslynCodeLens/Models/SymbolLocation.cs](../../src/RoslynCodeLens/Models/SymbolLocation.cs)
- Modify: [src/RoslynCodeLens/Models/SymbolContext.cs](../../src/RoslynCodeLens/Models/SymbolContext.cs)
- Create: [src/RoslynCodeLens/Models/SymbolOrigin.cs](../../src/RoslynCodeLens/Models/SymbolOrigin.cs)

**Step 1: Create the shared origin record.**

```csharp
namespace RoslynCodeLens.Models;

public record SymbolOrigin(
    string Kind,                 // "source" | "metadata"
    string? AssemblyName,        // null when Kind=="source"
    string? AssemblyVersion,     // null when Kind=="source"
    string? DocId);              // null when Kind=="source"; set for metadata symbols
```

**Step 2: Extend `SymbolLocation` with an optional `Origin` field.**

Add a nullable `SymbolOrigin? Origin = null` parameter at the end of the record. Records-with-defaults keep existing call sites compiling unchanged. No test failures expected yet.

**Step 3: Extend `SymbolContext` the same way.**

Add `SymbolOrigin? Origin = null` at the end.

**Step 4: Build to confirm nothing broke.**

Run: `dotnet build`
Expected: Build succeeds with zero warnings.

**Step 5: Commit.**

```
git add src/RoslynCodeLens/Models/SymbolOrigin.cs src/RoslynCodeLens/Models/SymbolLocation.cs src/RoslynCodeLens/Models/SymbolContext.cs
git commit -m "feat: add SymbolOrigin model for metadata symbol provenance"
```

---

## Task 2: Write failing test for MetadataSymbolResolver type lookup

**Files:**
- Test: `tests/RoslynCodeLens.Tests/Symbols/MetadataSymbolResolverTests.cs` (new)

**Step 1: Write the failing test.**

```csharp
using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Tests.Symbols;

public class MetadataSymbolResolverTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _sourceResolver = null!;
    private MetadataSymbolResolver _metadata = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _sourceResolver = new SymbolResolver(_loaded);
        _metadata = new MetadataSymbolResolver(_loaded, _sourceResolver);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Resolve_MetadataType_ReturnsMetadataSymbol()
    {
        var result = _metadata.Resolve(
            "Microsoft.Extensions.DependencyInjection.IServiceCollection");

        Assert.NotNull(result);
        Assert.Equal("metadata", result.Origin.Kind);
        Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions", result.Origin.AssemblyName);
        Assert.False(string.IsNullOrEmpty(result.Origin.AssemblyVersion));
        Assert.True(result.Symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface });
    }

    [Fact]
    public void Resolve_SourceTypeTakesPrecedence()
    {
        var result = _metadata.Resolve("IGreeter");

        Assert.NotNull(result);
        Assert.Equal("source", result.Origin.Kind);
    }

    [Fact]
    public void Resolve_UnknownSymbol_ReturnsNull()
    {
        Assert.Null(_metadata.Resolve("Nothing.Here.AtAll"));
    }

    [Fact]
    public void Resolve_MetadataMember_ReturnsMethodSymbol()
    {
        var result = _metadata.Resolve(
            "Microsoft.Extensions.DependencyInjection.IServiceCollection.Add");

        Assert.NotNull(result);
        Assert.Equal("metadata", result.Origin.Kind);
        Assert.IsAssignableFrom<IMethodSymbol>(result.Symbol);
    }
}
```

**Step 2: Run to verify it fails.**

Run: `dotnet test --filter "FullyQualifiedName~MetadataSymbolResolverTests"`
Expected: FAIL — `MetadataSymbolResolver` / namespace `RoslynCodeLens.Symbols` does not exist.

---

## Task 3: Implement MetadataSymbolResolver

**Files:**
- Create: `src/RoslynCodeLens/Symbols/MetadataSymbolResolver.cs`

**Step 1: Implement the class.**

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Symbols;

public sealed record ResolvedSymbol(ISymbol Symbol, SymbolOrigin Origin);

public sealed class MetadataSymbolResolver
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _source;

    public MetadataSymbolResolver(LoadedSolution loaded, SymbolResolver source)
    {
        _loaded = loaded;
        _source = source;
    }

    public ResolvedSymbol? Resolve(string name)
    {
        // 1. Source first — matches how the compilation binds.
        var sourceMatches = _source.FindSymbols(name);
        if (sourceMatches.Count > 0)
        {
            return new ResolvedSymbol(sourceMatches[0], SourceOrigin);
        }

        // 2. Metadata fallback.
        foreach (var compilation in _loaded.Compilations.Values)
        {
            var type = compilation.GetTypeByMetadataName(name);
            if (type != null && type.Locations.All(l => !l.IsInSource))
                return new ResolvedSymbol(type, ToOrigin(type));

            // Try Type.Member split.
            var lastDot = name.LastIndexOf('.');
            if (lastDot <= 0)
                continue;

            var typeName = name[..lastDot];
            var memberName = name[(lastDot + 1)..];
            var container = compilation.GetTypeByMetadataName(typeName);
            if (container == null || container.Locations.Any(l => l.IsInSource))
                continue;

            var member = container.GetMembers(memberName).FirstOrDefault();
            if (member != null)
                return new ResolvedSymbol(member, ToOrigin(member));
        }

        return null;
    }

    public IEnumerable<IAssemblySymbol> EnumerateMetadataAssemblies()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var compilation in _loaded.Compilations.Values)
        {
            foreach (var asm in compilation.GetReferencedAssemblySymbols())
            {
                if (seen.Add(asm.Identity.GetDisplayName()))
                    yield return asm;
            }
        }
    }

    public IAssemblySymbol? FindAssembly(string assemblyName)
    {
        foreach (var asm in EnumerateMetadataAssemblies())
        {
            if (string.Equals(asm.Identity.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                return asm;
        }
        return null;
    }

    public static SymbolOrigin ToOrigin(ISymbol symbol)
    {
        if (symbol.Locations.Any(l => l.IsInSource))
            return SourceOrigin;

        var asm = symbol.ContainingAssembly;
        return new SymbolOrigin(
            "metadata",
            asm?.Identity.Name,
            asm?.Identity.Version.ToString(),
            symbol.GetDocumentationCommentId());
    }

    public static SymbolOrigin SourceOrigin { get; } = new("source", null, null, null);
}
```

**Step 2: Run tests to verify they pass.**

Run: `dotnet test --filter "FullyQualifiedName~MetadataSymbolResolverTests"`
Expected: PASS (4 tests).

**Step 3: Commit.**

```
git add src/RoslynCodeLens/Symbols/MetadataSymbolResolver.cs tests/RoslynCodeLens.Tests/Symbols/MetadataSymbolResolverTests.cs
git commit -m "feat: MetadataSymbolResolver resolves source + metadata symbols by name"
```

---

## Task 4: Wire MetadataSymbolResolver into SolutionManager

**Files:**
- Modify: [src/RoslynCodeLens/SolutionManager.cs](../../src/RoslynCodeLens/SolutionManager.cs)

Currently `SolutionManager` exposes `GetResolver()` returning the source `SymbolResolver`. We add a parallel `GetMetadataResolver()` so tools don't need to instantiate it per-call.

**Step 1: Add a private field and build it alongside `_resolver`.**

In the constructor and in `WarmupAsync` / `RebuildStaleProjects` / `ForceReloadAsync`, wherever `new SymbolResolver(...)` is called, also build `new MetadataSymbolResolver(newLoaded, newResolver)` and assign to a new field `_metadataResolver`.

**Step 2: Add public accessor.**

```csharp
public MetadataSymbolResolver GetMetadataResolver()
{
    _warmupTask?.GetAwaiter().GetResult();
    if (_warmupException != null)
        throw new InvalidOperationException("Solution warmup failed.", _warmupException);
    RebuildIfStale();
    return _metadataResolver;
}
```

**Step 3: Also surface through `MultiSolutionManager`.**

Add `public MetadataSymbolResolver GetMetadataResolver()` to `MultiSolutionManager` delegating to the active `SolutionManager` (follow the existing `GetResolver()` pattern).

**Step 4: Build.**

Run: `dotnet build`
Expected: succeeds.

**Step 5: Commit.**

```
git add src/RoslynCodeLens/SolutionManager.cs src/RoslynCodeLens/MultiSolutionManager.cs
git commit -m "feat: expose MetadataSymbolResolver through SolutionManager"
```

---

## Task 5: Extend `go_to_definition` to surface metadata symbols

**Files:**
- Modify: [src/RoslynCodeLens/Tools/GoToDefinitionLogic.cs](../../src/RoslynCodeLens/Tools/GoToDefinitionLogic.cs)
- Modify: [src/RoslynCodeLens/Tools/GoToDefinitionTool.cs](../../src/RoslynCodeLens/Tools/GoToDefinitionTool.cs)
- Test: `tests/RoslynCodeLens.Tests/Tools/GoToDefinitionToolTests.cs`

**Step 1: Write failing test.**

Add to the existing test file:

```csharp
[Fact]
public void GoToDefinition_MetadataType_ReturnsMetadataOrigin()
{
    var result = GoToDefinitionLogic.Execute(
        _resolver, _metadata, "Microsoft.Extensions.DependencyInjection.IServiceCollection");

    var single = Assert.Single(result);
    Assert.NotNull(single.Origin);
    Assert.Equal("metadata", single.Origin!.Kind);
    Assert.Equal("", single.File);
    Assert.Equal(0, single.Line);
}
```

Also add `_metadata = new MetadataSymbolResolver(_loaded, _resolver);` to `InitializeAsync`. The test compiles once Step 2 changes the `Execute` signature.

**Step 2: Run — expected to fail with compile error.**

Run: `dotnet test --filter "FullyQualifiedName~GoToDefinitionToolTests"`
Expected: FAIL — Logic signature still takes only `SymbolResolver`.

**Step 3: Update `GoToDefinitionLogic.Execute` signature and body.**

```csharp
public static IReadOnlyList<SymbolLocation> Execute(
    SymbolResolver source, MetadataSymbolResolver metadata, string symbol)
{
    var sourceHits = source.FindSymbols(symbol);
    if (sourceHits.Count > 0)
    {
        return BuildSourceResults(source, sourceHits);
    }

    var resolved = metadata.Resolve(symbol);
    if (resolved == null)
        return [];

    return
    [
        new SymbolLocation(
            KindOf(resolved.Symbol),
            resolved.Symbol.ToDisplayString(),
            File: "",
            Line: 0,
            Project: "",
            IsGenerated: false,
            Origin: resolved.Origin)
    ];
}

private static IReadOnlyList<SymbolLocation> BuildSourceResults(
    SymbolResolver source, IReadOnlyList<ISymbol> symbols) { /* move current loop here, pass Origin: MetadataSymbolResolver.SourceOrigin */ }

private static string KindOf(ISymbol s) => /* existing switch extracted */;
```

**Step 4: Update the Tool wrapper** to pass `manager.GetMetadataResolver()` as the second argument.

**Step 5: Run test to verify pass.**

Run: `dotnet test --filter "FullyQualifiedName~GoToDefinitionToolTests"`
Expected: PASS (all pre-existing tests + new metadata test).

**Step 6: Commit.**

```
git add src/RoslynCodeLens/Tools/GoToDefinitionLogic.cs src/RoslynCodeLens/Tools/GoToDefinitionTool.cs tests/RoslynCodeLens.Tests/Tools/GoToDefinitionToolTests.cs
git commit -m "feat: go_to_definition surfaces metadata symbols with origin block"
```

---

## Task 6: Extend `get_symbol_context` to accept metadata types

**Files:**
- Modify: [src/RoslynCodeLens/Tools/GetSymbolContextLogic.cs](../../src/RoslynCodeLens/Tools/GetSymbolContextLogic.cs)
- Modify: [src/RoslynCodeLens/Tools/GetSymbolContextTool.cs](../../src/RoslynCodeLens/Tools/GetSymbolContextTool.cs)
- Test: `tests/RoslynCodeLens.Tests/Tools/GetSymbolContextToolTests.cs`

**Step 1: Failing test.**

```csharp
[Fact]
public void GetContext_MetadataInterface_ReturnsMembersAndOrigin()
{
    var result = GetSymbolContextLogic.Execute(
        _loaded, _resolver, _metadata, "Microsoft.Extensions.DependencyInjection.IServiceCollection");

    Assert.NotNull(result);
    Assert.Equal("metadata", result.Origin!.Kind);
    Assert.Equal("Microsoft.Extensions.DependencyInjection", result.Namespace);
    Assert.Empty(result.InjectedDependencies);
    Assert.NotEmpty(result.PublicMembers);
    Assert.Equal("", result.File);
    Assert.Equal(0, result.Line);
}
```

Wire `_metadata` in `InitializeAsync` as in Task 5.

**Step 2: Run — fail.**

**Step 3: Update `GetSymbolContextLogic.Execute`.**

Accept `MetadataSymbolResolver` parameter. If `resolver.FindNamedTypes(symbol)` is empty, call `metadata.Resolve(symbol)`; if it returns a type, build a `SymbolContext` from it with:
- `File = ""`, `Line = 0`, `Project = ""`
- `BaseClass` / `Interfaces` / `PublicMembers` from the `INamedTypeSymbol` as before
- `InjectedDependencies = []` (metadata-interface constructors are not meaningful in this feature)
- `Origin = resolved.Origin`

For source-origin results, set `Origin = MetadataSymbolResolver.SourceOrigin` so consumers can always read the field.

**Step 4: Update Tool wrapper to pass metadata resolver.**

**Step 5: Test passes.**

**Step 6: Commit.**

```
git commit -m "feat: get_symbol_context accepts metadata types"
```

---

## Task 7: Extend `get_type_overview` and `get_type_hierarchy` to metadata types

Repeat the Task-5/6 pattern for:

- [src/RoslynCodeLens/Tools/GetTypeOverviewLogic.cs](../../src/RoslynCodeLens/Tools/GetTypeOverviewLogic.cs)
- [src/RoslynCodeLens/Tools/GetTypeHierarchyLogic.cs](../../src/RoslynCodeLens/Tools/GetTypeHierarchyLogic.cs)

For `get_type_hierarchy` specifically: derived types stay source-only — the logic is "base chain from any type (source or metadata), derived types from source index only" because we cannot enumerate all derivations across the ecosystem. Document this in an XML-doc comment on `Execute`.

**Each tool gets one TDD cycle:**
1. Failing test using `Microsoft.Extensions.DependencyInjection.IServiceCollection` (has base `IEnumerable<ServiceDescriptor>`, useful inheritance)
2. Run → fail
3. Update Logic signature and body
4. Update Tool wrapper
5. Run → pass
6. Commit per tool: `feat: get_type_overview accepts metadata types`, `feat: get_type_hierarchy accepts metadata types`

---

## Task 8: Extend `find_attribute_usages` to look up metadata attribute types

**Files:**
- Modify: [src/RoslynCodeLens/Tools/FindAttributeUsagesLogic.cs](../../src/RoslynCodeLens/Tools/FindAttributeUsagesLogic.cs)
- Test: `tests/RoslynCodeLens.Tests/Tools/FindAttributeUsagesToolTests.cs`

Already works on attribute **usages** in source — the task is to let the *attribute type itself* be a metadata symbol. E.g. `find_attribute_usages("System.ObsoleteAttribute")` should resolve the metadata attribute and then scan source usage.

**Step 1: Failing test** — look up usages of `System.Diagnostics.CodeAnalysis.SuppressMessageAttribute` (present in `GlobalSuppressions.cs`).

**Step 2: Fail → update Logic to accept `MetadataSymbolResolver` and use it when the source resolver finds nothing → pass → commit.**

---

## Task 9: Extend `search_symbols` to include metadata types

**Files:**
- Modify: [src/RoslynCodeLens/Tools/SearchSymbolsLogic.cs](../../src/RoslynCodeLens/Tools/SearchSymbolsLogic.cs)
- Test: `tests/RoslynCodeLens.Tests/Tools/SearchSymbolsToolTests.cs`

Search currently walks `resolver.TypesBySimpleName` / `MembersBySimpleName`. We add a second pass that enumerates metadata assemblies from `MetadataSymbolResolver.EnumerateMetadataAssemblies()`, walks the `GlobalNamespace`, and emits `SymbolLocation` with `File=""`, `Line=0`, and `Origin` set to metadata.

**Step 1: Failing test.**

```csharp
[Fact]
public void Search_MatchesMetadataSymbol()
{
    var results = SearchSymbolsLogic.Execute(_resolver, _metadata, "IServiceCollection");
    Assert.Contains(results, r => r.Origin?.Kind == "metadata"
        && r.FullName == "Microsoft.Extensions.DependencyInjection.IServiceCollection");
}
```

**Step 2: Run → fail.**

**Step 3: Implement.**

Add a pass: `foreach assembly in metadata.EnumerateMetadataAssemblies()` → `SymbolResolver.GetAllTypes(assembly.GlobalNamespace)` → filter by `type.Name.Contains(query, OrdinalIgnoreCase)` → add to results with metadata origin. Respect the existing `MaxResults = 50` cap (source results take precedence).

Budget: to avoid walking all of `System.Private.CoreLib` on every search, skip assemblies where `IsImplicitlyDeclared` is true OR `Identity.Name` starts with `System.` / `Microsoft.` unless the source pass returned zero metadata hits *and* the query is ≥ 3 characters. Document the heuristic with a comment citing token-budget concern.

**Step 4: Run → pass.**

**Step 5: Commit.**

```
git commit -m "feat: search_symbols includes metadata types with budget heuristics"
```

---

## Task 10: Add `InspectExternalAssembly` model

**Files:**
- Create: `src/RoslynCodeLens/Models/ExternalAssemblyOverview.cs`
- Create: `src/RoslynCodeLens/Models/ExternalNamespaceOverview.cs`

**Step 1: Define records.**

```csharp
namespace RoslynCodeLens.Models;

public record ExternalAssemblyOverview(
    string Mode,                                             // "summary" | "namespace"
    string Name,
    string Version,
    string? TargetFramework,
    string? PublicKeyToken,
    IReadOnlyList<ExternalNamespaceSummary> NamespaceTree,   // non-empty for mode=="summary"
    IReadOnlyList<ExternalTypeInfo> Types);                  // non-empty for mode=="namespace"

public record ExternalNamespaceSummary(
    string Namespace,
    int TypeCount,
    IReadOnlyList<string> PublicTypeNames);

public record ExternalTypeInfo(
    string Kind,                   // "class" | "interface" | "struct" | "enum" | "delegate"
    string FullName,
    IReadOnlyList<string> Modifiers,
    string? BaseType,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<ExternalMemberInfo> Members,
    IReadOnlyList<string> Attributes,
    string? XmlDocSummary);

public record ExternalMemberInfo(
    string Kind,                   // "method" | "property" | "field" | "event" | "constructor"
    string Signature,
    string? XmlDocSummary);
```

**Step 2: Build.**

Run: `dotnet build`

**Step 3: Commit.**

```
git commit -m "feat: add ExternalAssemblyOverview models"
```

---

## Task 11: Write failing test for `inspect_external_assembly` summary mode

**Files:**
- Test: `tests/RoslynCodeLens.Tests/Tools/InspectExternalAssemblyToolTests.cs` (new)

```csharp
using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class InspectExternalAssemblyToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private MetadataSymbolResolver _metadata = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _metadata = new MetadataSymbolResolver(_loaded, new SymbolResolver(_loaded));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Summary_ListsNamespacesAndCounts()
    {
        var result = InspectExternalAssemblyLogic.Execute(
            _metadata, "Microsoft.Extensions.DependencyInjection.Abstractions",
            mode: "summary", namespaceFilter: null);

        Assert.NotNull(result);
        Assert.Equal("summary", result!.Mode);
        Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions", result.Name);
        Assert.NotEmpty(result.NamespaceTree);
        Assert.Contains(result.NamespaceTree,
            n => n.Namespace == "Microsoft.Extensions.DependencyInjection"
              && n.PublicTypeNames.Contains("IServiceCollection"));
        Assert.Empty(result.Types);
    }

    [Fact]
    public void Namespace_ReturnsTypesWithMembers()
    {
        var result = InspectExternalAssemblyLogic.Execute(
            _metadata, "Microsoft.Extensions.DependencyInjection.Abstractions",
            mode: "namespace", namespaceFilter: "Microsoft.Extensions.DependencyInjection");

        Assert.NotNull(result);
        Assert.Equal("namespace", result!.Mode);
        Assert.Contains(result.Types, t => t.FullName.EndsWith("IServiceCollection", StringComparison.Ordinal));
        var svc = result.Types.First(t => t.FullName.EndsWith("IServiceCollection", StringComparison.Ordinal));
        Assert.Equal("interface", svc.Kind);
        Assert.NotEmpty(svc.Members);
    }

    [Fact]
    public void UnreferencedAssembly_Throws()
    {
        Assert.Throws<ArgumentException>(() => InspectExternalAssemblyLogic.Execute(
            _metadata, "Some.NotReferenced.Library", mode: "summary", namespaceFilter: null));
    }

    [Fact]
    public void NamespaceMode_UnknownNamespace_Throws()
    {
        Assert.Throws<ArgumentException>(() => InspectExternalAssemblyLogic.Execute(
            _metadata, "Microsoft.Extensions.DependencyInjection.Abstractions",
            mode: "namespace", namespaceFilter: "NoSuch.Namespace"));
    }
}
```

**Step 2: Run → fail** (logic class does not exist).

---

## Task 12: Implement `InspectExternalAssemblyLogic`

**Files:**
- Create: `src/RoslynCodeLens/Tools/InspectExternalAssemblyLogic.cs`

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class InspectExternalAssemblyLogic
{
    public static ExternalAssemblyOverview Execute(
        MetadataSymbolResolver metadata,
        string assemblyName,
        string mode,
        string? namespaceFilter)
    {
        var assembly = metadata.FindAssembly(assemblyName)
            ?? throw new ArgumentException(
                $"Assembly '{assemblyName}' is not referenced by any project in the active solution. " +
                "Use get_nuget_dependencies to list referenced assemblies.");

        var identity = assembly.Identity;
        return mode switch
        {
            "summary" => BuildSummary(assembly),
            "namespace" => BuildNamespace(assembly, namespaceFilter
                ?? throw new ArgumentException("'namespace' is required when mode='namespace'.")),
            _ => throw new ArgumentException($"Unknown mode '{mode}'. Expected 'summary' or 'namespace'.")
        };
    }

    private static ExternalAssemblyOverview BuildSummary(IAssemblySymbol assembly)
    {
        var byNamespace = new SortedDictionary<string, List<INamedTypeSymbol>>(StringComparer.Ordinal);
        foreach (var type in SymbolResolver.GetAllTypes(assembly.GlobalNamespace))
        {
            if (type.DeclaredAccessibility != Accessibility.Public || type.ContainingType != null)
                continue;
            var ns = type.ContainingNamespace.ToDisplayString();
            if (!byNamespace.TryGetValue(ns, out var list))
                byNamespace[ns] = list = new List<INamedTypeSymbol>();
            list.Add(type);
        }

        var tree = byNamespace.Select(kv => new ExternalNamespaceSummary(
            kv.Key, kv.Value.Count,
            kv.Value.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList()))
            .ToList();

        var id = assembly.Identity;
        return new ExternalAssemblyOverview(
            "summary", id.Name, id.Version.ToString(),
            TargetFramework: null, PublicKeyToken: FormatPublicKey(id.PublicKeyToken),
            NamespaceTree: tree, Types: []);
    }

    private static ExternalAssemblyOverview BuildNamespace(IAssemblySymbol assembly, string ns)
    {
        var types = new List<INamedTypeSymbol>();
        foreach (var t in SymbolResolver.GetAllTypes(assembly.GlobalNamespace))
        {
            if (t.DeclaredAccessibility != Accessibility.Public || t.ContainingType != null)
                continue;
            if (!string.Equals(t.ContainingNamespace.ToDisplayString(), ns, StringComparison.Ordinal))
                continue;
            types.Add(t);
        }

        if (types.Count == 0)
            throw new ArgumentException(
                $"Namespace '{ns}' not found (or contains no public types) in assembly '{assembly.Identity.Name}'.");

        var typeInfos = types.OrderBy(t => t.Name, StringComparer.Ordinal).Select(ToTypeInfo).ToList();

        var id = assembly.Identity;
        return new ExternalAssemblyOverview(
            "namespace", id.Name, id.Version.ToString(),
            TargetFramework: null, PublicKeyToken: FormatPublicKey(id.PublicKeyToken),
            NamespaceTree: [], Types: typeInfos);
    }

    private static ExternalTypeInfo ToTypeInfo(INamedTypeSymbol t)
    {
        var kind = t.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Struct => "struct",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => "class"
        };
        var modifiers = new List<string>();
        if (t.IsAbstract && t.TypeKind != TypeKind.Interface) modifiers.Add("abstract");
        if (t.IsSealed) modifiers.Add("sealed");
        if (t.IsStatic) modifiers.Add("static");

        var baseType = t.BaseType is { SpecialType: not SpecialType.System_Object }
            ? t.BaseType.ToDisplayString() : null;
        var interfaces = t.Interfaces.Select(i => i.ToDisplayString()).ToList();

        var members = new List<ExternalMemberInfo>();
        foreach (var m in t.GetMembers())
        {
            if (m.DeclaredAccessibility != Accessibility.Public || m.IsImplicitlyDeclared)
                continue;
            if (m is IMethodSymbol { MethodKind:
                MethodKind.PropertyGet or MethodKind.PropertySet or
                MethodKind.EventAdd or MethodKind.EventRemove })
                continue;
            members.Add(new ExternalMemberInfo(
                MemberKind(m),
                m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                SummaryFromXmlDoc(m)));
        }

        var attributes = t.GetAttributes()
            .Select(a => a.AttributeClass?.Name ?? "Attribute")
            .ToList();

        return new ExternalTypeInfo(kind, t.ToDisplayString(), modifiers, baseType,
            interfaces, members, attributes, SummaryFromXmlDoc(t));
    }

    private static string MemberKind(ISymbol s) => s switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor } => "constructor",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => "symbol"
    };

    private static string? SummaryFromXmlDoc(ISymbol s)
    {
        var xml = s.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;
        var start = xml.IndexOf("<summary>", StringComparison.Ordinal);
        var end = xml.IndexOf("</summary>", StringComparison.Ordinal);
        if (start < 0 || end <= start)
            return null;
        return xml.Substring(start + "<summary>".Length, end - start - "<summary>".Length).Trim();
    }

    private static string? FormatPublicKey(System.Collections.Immutable.ImmutableArray<byte> key)
        => key.IsDefaultOrEmpty ? null : string.Concat(key.Select(b => b.ToString("x2")));
}
```

**Step 2: Run tests.**

Run: `dotnet test --filter "FullyQualifiedName~InspectExternalAssemblyToolTests"`
Expected: PASS.

**Step 3: Commit.**

```
git commit -m "feat: inspect_external_assembly logic with summary + namespace modes"
```

---

## Task 13: Wire `inspect_external_assembly` as an MCP tool

**Files:**
- Create: `src/RoslynCodeLens/Tools/InspectExternalAssemblyTool.cs`

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class InspectExternalAssemblyTool
{
    [McpServerTool(Name = "inspect_external_assembly"),
     Description("Inspect a referenced closed-source assembly. mode='summary' returns namespaces + type counts; mode='namespace' returns public types and members for the given namespace.")]
    public static ExternalAssemblyOverview Execute(
        MultiSolutionManager manager,
        [Description("Assembly name, e.g. 'Newtonsoft.Json' or 'Microsoft.Extensions.DependencyInjection.Abstractions'")] string assemblyName,
        [Description("'summary' (default) or 'namespace'")] string mode = "summary",
        [Description("Required when mode='namespace'")] string? namespaceFilter = null)
    {
        manager.EnsureLoaded();
        return InspectExternalAssemblyLogic.Execute(
            manager.GetMetadataResolver(), assemblyName, mode, namespaceFilter);
    }
}
```

**Step 1: Manual smoke test** — run the server against a small solution and call the tool via `dotnet tool run roslyn-codelens-mcp` behind the MCP client. Can be skipped if test coverage is sufficient; reviewers will exercise in Claude Code.

**Step 2: Commit.**

```
git commit -m "feat: register inspect_external_assembly MCP tool"
```

---

## Task 14: Update SKILL.md with Phase-1 guidance

**Files:**
- Modify: [plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md](../../plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md)

**Step 1: Add "Working with external assemblies" section.** Topics:
- What counts as external (symbol's `origin.kind === "metadata"`).
- Fully-qualified name addressing.
- Origin block format (`{ kind, assemblyName, assemblyVersion, docId }`).

**Step 2: Add per-tool metadata behavior table.** One row per tool with three columns: *Works on metadata*, *Caveats*, *Alternative if not supported*. Include **all** existing tools plus the new `inspect_external_assembly`. Mark Tier-3 tools explicitly as *No — source only*.

**Step 3: Document `inspect_external_assembly`** with two worked examples:
- summary → drill into one namespace
- summary alone is enough for a "what does this package expose" read

**Step 4: Decision tree at top of the skill.**

```
Symbol in my source      → existing tools (work unchanged)
External symbol by name  → existing tools (Tier 1). They return origin="metadata".
Who in my code uses it?  → deferred to Phase 2
Browse a package         → inspect_external_assembly
See a method's IL        → deferred to Phase 3
Browse arbitrary DLL     → add temp ProjectReference to throwaway project, reload
```

**Step 5: Commit.**

```
git commit -m "docs(skill): add external-assembly guidance for Phase 1 tools"
```

---

## Task 15: Full test + build sweep and PR prep

**Step 1: Run everything.**

Run: `dotnet build && dotnet test`
Expected: All tests pass, zero warnings.

**Step 2: Confirm `docs/features.md` and `docs/plans/` design doc are consistent.** If any Phase-1 design detail changed during implementation, update [docs/plans/2026-04-24-closed-source-assemblies-design.md](./2026-04-24-closed-source-assemblies-design.md) to match before PR.

**Step 3: Use @superpowers:verification-before-completion before declaring done.** Confirm the tool is actually callable end-to-end via MCP before merge.

**Step 4: Open PR titled `feat: external-assembly analysis — Phase 1 (Tier 1 + inspect_external_assembly)`** linking issue #95 and the design doc.
