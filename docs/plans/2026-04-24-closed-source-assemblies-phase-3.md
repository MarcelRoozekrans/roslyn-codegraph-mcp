# Phase 3 — peek_il (IL disassembly for closed-source methods)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a new `peek_il` MCP tool that returns ilasm-style textual IL for a single method in a metadata assembly. This is the only Phase that adds a new library dependency.

**Architecture:** Resolve the input to an `IMethodSymbol` via `MetadataSymbolResolver`. Locate the `PortableExecutableReference` matching the method's containing assembly. Open the DLL via `System.Reflection.Metadata.PEReader` and hand to `ICSharpCode.Decompiler.Disassembler.ReflectionDisassembler`, which emits ilasm-style text. Output is a single string wrapped in a minimal result record. A `PEFileCache` keeps `PEFile` handles open across calls to the same DLL and invalidates on file-timestamp change.

**Tech Stack:** Adds `ICSharpCode.Decompiler` (LGPL-2.1) to [src/RoslynCodeLens/RoslynCodeLens.csproj](../../src/RoslynCodeLens/RoslynCodeLens.csproj). Only the disassembler namespace is consumed; no decompiler types are referenced.

**Prerequisite:** Phase 1 merged (`MetadataSymbolResolver`, `GetMetadataResolver()`).

**Fixture:** `Microsoft.Extensions.DependencyInjection.Abstractions` `ServiceDescriptor` constructor has simple, deterministic IL — good target for assertions.

**Design reference:** [docs/plans/2026-04-24-closed-source-assemblies-design.md](./2026-04-24-closed-source-assemblies-design.md)

---

## Task 1: Add the ICSharpCode.Decompiler dependency

**Files:**
- Modify: [src/RoslynCodeLens/RoslynCodeLens.csproj](../../src/RoslynCodeLens/RoslynCodeLens.csproj)
- Modify: `README.md` (add attribution)
- Create: `NOTICE` (if absent) with LGPL attribution

**Step 1: Add PackageReference.**

Use the latest 10.x release of `ICSharpCode.Decompiler` (as of 2026-04: 10.x line). If CI build fails due to framework mismatch against net10, pin to whichever version targets `netstandard2.0` / `net8.0` (we ship on net10 which consumes both).

```xml
<PackageReference Include="ICSharpCode.Decompiler" Version="10.x.y" />
```

**Step 2: Build.**

Run: `dotnet build`
Expected: succeeds. Warnings from the library itself should be filtered via `<NoWarn>` if necessary, but only for the library's own assemblies.

**Step 3: Add attribution.** In `README.md` under a new "Third-party licenses" heading, add:

> This project uses [ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy) for IL disassembly, licensed under the MIT license (from version 10 onwards) — verify current license at the linked repository before merging and adjust this note if needed.

Create `NOTICE` at repo root if not already present, listing the dependency.

**Step 4: Commit.**

```
git commit -m "chore: add ICSharpCode.Decompiler dependency for peek_il"
```

---

## Task 2: Add result model

**Files:**
- Create: `src/RoslynCodeLens/Models/IlPeekResult.cs`

```csharp
namespace RoslynCodeLens.Models;

public record IlPeekResult(
    string MethodFullName,
    string AssemblyName,
    string AssemblyVersion,
    string Il);        // ilasm-style text, possibly multi-line
```

Commit: `git commit -m "feat: add IlPeekResult model"`

---

## Task 3: PEFileCache keyed by (path, lastWriteTime)

**Files:**
- Create: `src/RoslynCodeLens/Metadata/PEFileCache.cs`
- Test: `tests/RoslynCodeLens.Tests/Metadata/PEFileCacheTests.cs`

**Step 1: Failing test.**

```csharp
using ICSharpCode.Decompiler.Metadata;
using RoslynCodeLens.Metadata;

namespace RoslynCodeLens.Tests.Metadata;

public class PEFileCacheTests
{
    [Fact]
    public void Get_SamePath_ReturnsSameInstance()
    {
        var cache = new PEFileCache();
        var path = typeof(object).Assembly.Location;

        var first = cache.Get(path);
        var second = cache.Get(path);

        Assert.Same(first, second);
    }

    [Fact]
    public void Invalidate_DropsEntry()
    {
        var cache = new PEFileCache();
        var path = typeof(object).Assembly.Location;

        var first = cache.Get(path);
        cache.Invalidate(path);
        var second = cache.Get(path);

        Assert.NotSame(first, second);
    }
}
```

**Step 2: Fail** — class does not exist.

**Step 3: Implement.**

```csharp
using System.Collections.Concurrent;
using ICSharpCode.Decompiler.Metadata;

namespace RoslynCodeLens.Metadata;

public sealed class PEFileCache : IDisposable
{
    private readonly ConcurrentDictionary<string, (DateTime Timestamp, PEFile File)> _cache
        = new(StringComparer.OrdinalIgnoreCase);

    public PEFile Get(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_cache.TryGetValue(path, out var existing) && existing.Timestamp == stamp)
            return existing.File;

        var pe = new PEFile(path);
        _cache[path] = (stamp, pe);
        return pe;
    }

    public void Invalidate(string path)
    {
        if (_cache.TryRemove(path, out var entry))
            entry.File.Dispose();
    }

    public void Dispose()
    {
        foreach (var entry in _cache.Values)
            entry.File.Dispose();
        _cache.Clear();
    }
}
```

**Step 4: Pass.** Commit: `git commit -m "feat: PEFileCache with timestamp-based invalidation"`.

---

## Task 4: IlDisassemblerAdapter — isolates ICSharpCode.Decompiler types

**Files:**
- Create: `src/RoslynCodeLens/Metadata/IlDisassemblerAdapter.cs`

**Step 1: Implement.**

```csharp
using System.IO;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;

namespace RoslynCodeLens.Metadata;

public sealed class IlDisassemblerAdapter
{
    private readonly PEFileCache _cache;

    public IlDisassemblerAdapter(PEFileCache cache) { _cache = cache; }

    public string DisassembleMethod(string assemblyPath, int metadataToken)
    {
        var pe = _cache.Get(assemblyPath);
        using var writer = new StringWriter();
        var output = new PlainTextOutput(writer);
        var disasm = new ReflectionDisassembler(output, cancellationToken: default)
        {
            DetectControlStructure = true,
            ShowSequencePoints = false,
        };
        var handle = MetadataTokens.EntityHandle(metadataToken);
        disasm.DisassembleMethod(pe, (System.Reflection.Metadata.MethodDefinitionHandle)handle);
        return writer.ToString();
    }
}
```

No TDD cycle here — this wrapper is a thin adapter exercised through `PeekIlLogicTests` in Task 6. Verify it at least compiles.

**Step 2: Commit.**

```
git commit -m "feat: IlDisassemblerAdapter wraps ICSharpCode.Decompiler"
```

---

## Task 5: Write failing test for PeekIlLogic

**Files:**
- Test: `tests/RoslynCodeLens.Tests/Tools/PeekIlToolTests.cs`

```csharp
using RoslynCodeLens;
using RoslynCodeLens.Metadata;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class PeekIlToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;
    private MetadataSymbolResolver _metadata = null!;
    private IlDisassemblerAdapter _adapter = null!;
    private PEFileCache _peCache = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _resolver = new SymbolResolver(_loaded);
        _metadata = new MetadataSymbolResolver(_loaded, _resolver);
        _peCache = new PEFileCache();
        _adapter = new IlDisassemblerAdapter(_peCache);
    }

    public Task DisposeAsync() { _peCache.Dispose(); return Task.CompletedTask; }

    [Fact]
    public void Peek_MetadataCtor_ReturnsIlText()
    {
        // Pick an overload deterministically — ServiceDescriptor has a well-known ctor taking (Type, Type).
        var result = PeekIlLogic.Execute(
            _loaded, _metadata, _adapter,
            "Microsoft.Extensions.DependencyInjection.ServiceDescriptor..ctor(System.Type, System.Type, Microsoft.Extensions.DependencyInjection.ServiceLifetime)");

        Assert.NotNull(result);
        Assert.Contains("IL_", result!.Il, StringComparison.Ordinal); // labels like IL_0000
        Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions", result.AssemblyName);
    }

    [Fact]
    public void Peek_SourceSymbol_Throws()
    {
        Assert.Throws<ArgumentException>(() => PeekIlLogic.Execute(
            _loaded, _metadata, _adapter, "Greeter.Greet"));
    }

    [Fact]
    public void Peek_AbstractMethod_Throws()
    {
        // Interface methods have no bodies.
        Assert.Throws<ArgumentException>(() => PeekIlLogic.Execute(
            _loaded, _metadata, _adapter,
            "Microsoft.Extensions.DependencyInjection.IServiceCollection.Add"));
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~PeekIlToolTests"`
Expected: FAIL — `PeekIlLogic` does not exist.

---

## Task 6: Implement PeekIlLogic

**Files:**
- Create: `src/RoslynCodeLens/Tools/PeekIlLogic.cs`

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Metadata;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class PeekIlLogic
{
    public static IlPeekResult Execute(
        LoadedSolution loaded,
        MetadataSymbolResolver metadata,
        IlDisassemblerAdapter disassembler,
        string methodSymbol)
    {
        var resolved = metadata.Resolve(methodSymbol)
            ?? throw new ArgumentException(
                $"Symbol '{methodSymbol}' not found.");

        if (resolved.Origin.Kind != "metadata")
            throw new ArgumentException(
                $"Symbol '{methodSymbol}' is a source symbol. Use go_to_definition instead.");

        if (resolved.Symbol is not IMethodSymbol method)
            throw new ArgumentException(
                $"Symbol '{methodSymbol}' is not a method. peek_il only works on methods.");

        if (method.IsAbstract || method.MethodKind == MethodKind.EventAdd
            || method.MethodKind == MethodKind.EventRemove
            || method.ContainingType?.TypeKind == TypeKind.Interface && !method.IsStatic)
            throw new ArgumentException(
                $"Method '{methodSymbol}' has no body (abstract, interface instance member, or event accessor).");

        var assembly = method.ContainingAssembly
            ?? throw new ArgumentException("Could not determine containing assembly.");

        var assemblyPath = FindAssemblyPath(loaded, assembly)
            ?? throw new ArgumentException(
                $"Could not locate on-disk path for assembly '{assembly.Identity.Name}'.");

        var token = MetadataTokenOf(method)
            ?? throw new ArgumentException("Could not determine metadata token for method.");

        var ilText = disassembler.DisassembleMethod(assemblyPath, token);

        return new IlPeekResult(
            method.ToDisplayString(),
            assembly.Identity.Name,
            assembly.Identity.Version.ToString(),
            ilText);
    }

    private static string? FindAssemblyPath(LoadedSolution loaded, IAssemblySymbol assembly)
    {
        foreach (var compilation in loaded.Compilations.Values)
        {
            foreach (var reference in compilation.References)
            {
                if (reference is not PortableExecutableReference pe)
                    continue;

                var refAsm = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
                if (refAsm != null && SymbolEqualityComparer.Default.Equals(refAsm, assembly))
                    return pe.FilePath;
            }
        }
        return null;
    }

    private static int? MetadataTokenOf(IMethodSymbol method)
    {
        // Use reflection on the internal Roslyn `MetadataToken` via
        // `Microsoft.CodeAnalysis.PEMethodSymbol` exposed via `MetadataTokenAttribute`.
        // Simpler and stable: use SymbolDocumentationCommentId + manual resolution in the PE,
        // but easiest is using the Roslyn public surface:
        //   method.GetType().GetProperty("MetadataToken")?.GetValue(method)
        // which works for PEMethodSymbol in practice.
        var prop = method.GetType().GetProperty("MetadataToken",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance);
        if (prop?.GetValue(method) is int token)
            return token;
        return null;
    }
}
```

**Note for the implementer:** the reflection hop in `MetadataTokenOf` is fragile. If Roslyn 4.14 exposes this via a public API (e.g. through `ISymbol.MetadataToken` on a future revision), prefer that. If the reflection path returns 0 or null for some symbols, fall back to matching via `DocumentationCommentId` against the metadata reader's method definitions — loop `metadataReader.MethodDefinitions`, find the one whose full name matches. Cover this fallback path only if the happy test fails.

**Step 2: Run tests.**

Run: `dotnet test --filter "FullyQualifiedName~PeekIlToolTests"`
Expected: PASS.

**Step 3: Commit.**

```
git commit -m "feat: PeekIlLogic resolves metadata methods and returns ilasm text"
```

---

## Task 7: Register peek_il as an MCP tool

**Files:**
- Create: `src/RoslynCodeLens/Tools/PeekIlTool.cs`
- Modify: [src/RoslynCodeLens/SolutionManager.cs](../../src/RoslynCodeLens/SolutionManager.cs) — add `PEFileCache` and `IlDisassemblerAdapter` singletons; dispose on `Dispose()`.

**Step 1: Tool wrapper.**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class PeekIlTool
{
    [McpServerTool(Name = "peek_il"),
     Description("Return ilasm-style IL for a method in a referenced closed-source assembly.")]
    public static IlPeekResult Execute(
        MultiSolutionManager manager,
        [Description("Fully qualified method name with parameter types, e.g. 'Newtonsoft.Json.JsonConvert.SerializeObject(object)'")] string methodSymbol)
    {
        manager.EnsureLoaded();
        return PeekIlLogic.Execute(
            manager.GetLoadedSolution(),
            manager.GetMetadataResolver(),
            manager.GetIlDisassembler(),
            methodSymbol);
    }
}
```

**Step 2: Add `PEFileCache` / `IlDisassemblerAdapter` to `SolutionManager`.** Create them in the constructor, expose `GetIlDisassembler()`, dispose in `Dispose()`. Also surface `GetIlDisassembler()` on `MultiSolutionManager`.

**Step 3: Wire PEFileCache to FileChangeTracker.** Currently `FileChangeTracker` watches `*.cs / *.csproj / *.props / *.targets`. Add `*.dll` to the watched extensions so that when a NuGet restore or internal-assembly rebuild replaces a DLL, `_peCache.Invalidate(path)` is called.

Update `FileChangeTracker` to accept a callback:
```csharp
public Action<string>? OnAssemblyChanged { get; set; }
```

In the `ProcessPendingChanges` / `OnFileChangedPath` path, if the file is a `.dll`, invoke the callback with the full path (no project marking). Wire the callback in `SolutionManager` to call `_peCache.Invalidate(...)`.

**Step 4: Build + full test run.**

Run: `dotnet build && dotnet test`
Expected: all green.

**Step 5: Commit.**

```
git commit -m "feat: register peek_il MCP tool and wire PEFileCache to FileChangeTracker"
```

---

## Task 8: Add benchmarks for peek_il and inspect_external_assembly

**Files:**
- Modify: [benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs](../../benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs)

Add three benchmarks following the existing pattern:

```csharp
[Benchmark(Description = "inspect_external_assembly: summary")]
public object InspectExternalAssemblySummary() =>
    InspectExternalAssemblyLogic.Execute(_metadata,
        "Microsoft.Extensions.DependencyInjection.Abstractions", "summary", null);

[Benchmark(Description = "inspect_external_assembly: namespace")]
public object InspectExternalAssemblyNamespace() =>
    InspectExternalAssemblyLogic.Execute(_metadata,
        "Microsoft.Extensions.DependencyInjection.Abstractions", "namespace",
        "Microsoft.Extensions.DependencyInjection");

[Benchmark(Description = "peek_il: ServiceDescriptor ctor")]
public object PeekIlServiceDescriptor() =>
    PeekIlLogic.Execute(_loaded, _metadata, _ilAdapter,
        "Microsoft.Extensions.DependencyInjection.ServiceDescriptor..ctor(System.Type, System.Type, Microsoft.Extensions.DependencyInjection.ServiceLifetime)");
```

Add `_metadata`, `_ilAdapter`, `_peCache` fields and initialize them in `Setup()`.

**Step 1: Build benchmarks.**

Run: `dotnet build benchmarks/RoslynCodeLens.Benchmarks -c Release`

**Step 2: Smoke-run.**

Run: `dotnet run --project benchmarks/RoslynCodeLens.Benchmarks -c Release -- --filter '*inspect_external*' --job short`

Just verify no exceptions. Short-job results are not meaningful — we only want to confirm the benchmark code is wired correctly.

**Step 3: Commit.**

```
git commit -m "bench: add inspect_external_assembly and peek_il benchmarks"
```

---

## Task 9: SKILL.md — Phase 3 final pass

**Files:**
- Modify: [plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md](../../plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md)

**Step 1: Document peek_il.** A section with:
- Input format (fully qualified method with parameter types)
- Output is ilasm text — not C#
- When to use (after `find_callers` identifies a metadata method that's worth reading)
- Limitations (no abstract/interface instance members, no properties-as-whole — must target accessor)

**Step 2: Update the decision tree**: replace `"See a method's IL → deferred to Phase 3"` with `"See a method's IL → peek_il with the full signature"`.

**Step 3: Commit.**

```
git commit -m "docs(skill): document peek_il"
```

---

## Task 10: Final sweep + PR

**Step 1:** `dotnet build && dotnet test` — all green, zero warnings.

**Step 2:** @superpowers:verification-before-completion — call `peek_il` on a real dependency in Claude Code, confirm IL appears and is sensible.

**Step 3:** PR titled `feat: external-assembly analysis — Phase 3 (peek_il)` linking issue #95, design doc, and Phase 1 + Phase 2 PRs. Note in the PR description: new LGPL dependency (`ICSharpCode.Decompiler`), attribution added to `README.md` and `NOTICE`.

**Step 4:** Close issue #95 with a summary of all three phases shipped.
