# Phase 2 — Tier 2 references (source-to-metadata)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Allow `find_references`, `find_callers`, and `find_implementations` to accept metadata symbol names as input and find source call/reference/implementation sites that target them.

**Architecture:** Each tool's `*Logic.Execute` currently resolves its target symbol exclusively through the source `SymbolResolver`. We widen each one to fall back on `MetadataSymbolResolver.Resolve` when no source match is found. The downstream scan logic (iterating compilations, calling `GetSymbolInfo`, matching with `SymbolEqualityComparer`) already works for metadata symbols — the `ISymbol` identity comparison is uniform across source and metadata origins.

**Tech Stack:** Same as Phase 1. No new NuGet dependencies.

**Fixture:** `TestLib2` calls `services.AddSingleton<...>()` and similar `Microsoft.Extensions.DependencyInjection` APIs in `DiSetup.cs` / `GreeterConsumer.cs`. Those are real source-to-metadata references suitable for assertions. Verify by `Grep`-ing `AddSingleton`, `AddScoped`, `AddTransient` before writing tests.

**Prerequisite:** Phase 1 merged (`MetadataSymbolResolver`, `GetMetadataResolver()` on `MultiSolutionManager`, extended Tier-1 tools, `SymbolLocation.Origin`).

**Design reference:** [docs/plans/2026-04-24-closed-source-assemblies-design.md](./2026-04-24-closed-source-assemblies-design.md)

---

## Task 1: Confirm fixture has source-to-metadata references

**Step 1: Grep.**

Run: `grep -rn "AddSingleton\|AddScoped\|AddTransient\|IServiceCollection" tests/RoslynCodeLens.Tests/Fixtures/TestSolution`
Expected: At least one call site in `TestLib2/DiSetup.cs` or `GreeterConsumer.cs`.

**Step 2: If nothing matches,** add the minimum fixture to `TestLib2/DiSetup.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace TestLib2;

public static class DiSetup
{
    public static IServiceCollection AddGreeters(IServiceCollection services)
    {
        services.AddSingleton<TestLib.IGreeter, TestLib.Greeter>();
        return services;
    }
}
```

Only commit fixture changes if the grep failed. **No commit** otherwise.

---

## Task 2: Extend `find_references` to accept metadata symbols

**Files:**
- Modify: [src/RoslynCodeLens/Tools/FindReferencesLogic.cs](../../src/RoslynCodeLens/Tools/FindReferencesLogic.cs)
- Modify: [src/RoslynCodeLens/Tools/FindReferencesTool.cs](../../src/RoslynCodeLens/Tools/FindReferencesTool.cs)
- Test: [tests/RoslynCodeLens.Tests/Tools/FindReferencesToolTests.cs](../../tests/RoslynCodeLens.Tests/Tools/FindReferencesToolTests.cs)

**Step 1: Failing test.**

```csharp
[Fact]
public void FindReferences_MetadataInterface_FindsSourceUsages()
{
    var results = FindReferencesLogic.Execute(
        _loaded, _resolver, _metadata,
        "Microsoft.Extensions.DependencyInjection.IServiceCollection");

    Assert.NotEmpty(results);
    Assert.All(results, r => Assert.True(!string.IsNullOrEmpty(r.File)));
}
```

Wire `_metadata = new MetadataSymbolResolver(_loaded, _resolver);` in `InitializeAsync`.

**Step 2: Run → fail** (signature mismatch: Logic.Execute takes 3 args today).

**Step 3: Update `FindReferencesLogic.Execute` signature and body.**

```csharp
public static IReadOnlyList<SymbolReference> Execute(
    LoadedSolution loaded, SymbolResolver source, MetadataSymbolResolver metadata, string symbol)
{
    var targets = source.FindSymbols(symbol);
    if (targets.Count == 0)
    {
        var resolved = metadata.Resolve(symbol);
        if (resolved == null)
            return [];
        targets = [resolved.Symbol];
    }

    var targetSet = BuildTargetSet(targets);
    return ScanForReferences(loaded, source, targetSet);
}
```

No other change to `ScanForReferences` — it already uses `SymbolEqualityComparer` which works equally for metadata symbols.

**Step 4: Update `FindReferencesTool.Execute` to pass `manager.GetMetadataResolver()`.**

**Step 5: Run test → pass.** Verify pre-existing tests still pass.

Run: `dotnet test --filter "FullyQualifiedName~FindReferencesToolTests"`

**Step 6: Commit.**

```
git commit -m "feat: find_references accepts metadata symbols (source call sites of external symbols)"
```

---

## Task 3: Extend `find_callers` to accept metadata methods

**Files:**
- Modify: [src/RoslynCodeLens/Tools/FindCallersLogic.cs](../../src/RoslynCodeLens/Tools/FindCallersLogic.cs)
- Modify: [src/RoslynCodeLens/Tools/FindCallersTool.cs](../../src/RoslynCodeLens/Tools/FindCallersTool.cs)
- Test: [tests/RoslynCodeLens.Tests/Tools/FindCallersToolTests.cs](../../tests/RoslynCodeLens.Tests/Tools/FindCallersToolTests.cs)

**Step 1: Failing test.**

```csharp
[Fact]
public void FindCallers_MetadataExtensionMethod_FindsSourceInvocations()
{
    // ServiceCollectionServiceExtensions.AddSingleton is the extension target.
    var results = FindCallersLogic.Execute(
        _loaded, _resolver, _metadata,
        "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton");

    Assert.NotEmpty(results);
}
```

**Step 2: Fail → update signature.**

The current `Execute` uses `resolver.FindMethods(symbol)`. Widen:

```csharp
public static IReadOnlyList<CallerInfo> Execute(
    LoadedSolution loaded, SymbolResolver source, MetadataSymbolResolver metadata, string symbol)
{
    var targetMethods = source.FindMethods(symbol);
    if (targetMethods.Count == 0)
    {
        var resolved = metadata.Resolve(symbol);
        if (resolved?.Symbol is IMethodSymbol m)
            targetMethods = [m];
        else
            return [];
    }

    // ... rest unchanged
}
```

**Step 3: Update Tool wrapper, run tests, commit.**

```
git commit -m "feat: find_callers accepts metadata methods"
```

---

## Task 4: Extend `find_implementations` to accept metadata interfaces

**Files:**
- Modify: [src/RoslynCodeLens/Tools/FindImplementationsLogic.cs](../../src/RoslynCodeLens/Tools/FindImplementationsLogic.cs)
- Modify: [src/RoslynCodeLens/Tools/FindImplementationsTool.cs](../../src/RoslynCodeLens/Tools/FindImplementationsTool.cs)
- Test: [tests/RoslynCodeLens.Tests/Tools/FindImplementationsToolTests.cs](../../tests/RoslynCodeLens.Tests/Tools/FindImplementationsToolTests.cs)

The current implementation uses `resolver.GetInterfaceImplementors(target)` / `GetDerivedTypes(target)` — both indexed by `INamedTypeSymbol` instance. Source-only lookups. For metadata interfaces, we need a different path because the **implementors live in source** but the key is a metadata symbol.

**Step 1: Failing test.**

```csharp
[Fact]
public void FindImplementations_MetadataInterface_FindsSourceImplementors()
{
    // Fixture: TestLib/Greeter.cs implements TestLib/IGreeter (source interface).
    // We want a metadata-interface example. Adjust fixture if needed to reference e.g.
    // System.IDisposable, or use Microsoft.Extensions.Hosting IHostedService (not in deps).
    // Simpler: add IDisposable to Greeter in the fixture and assert.
    var results = FindImplementationsLogic.Execute(
        _loaded, _resolver, _metadata, "System.IDisposable");

    Assert.Contains(results, r => r.FullName.EndsWith("Greeter", StringComparison.Ordinal));
}
```

If the fixture does not implement any metadata interface, **add a tiny fixture change**: make `TestLib/Greeter.cs` implement `IDisposable` with an empty `Dispose()`. That change is in-scope and makes the test deterministic.

**Step 2: Fail → update signature.**

```csharp
public static IReadOnlyList<SymbolLocation> Execute(
    LoadedSolution loaded, SymbolResolver source, MetadataSymbolResolver metadata, string symbol)
{
    var targets = source.FindNamedTypes(symbol);
    if (targets.Count == 0)
    {
        var resolved = metadata.Resolve(symbol);
        if (resolved?.Symbol is INamedTypeSymbol nt)
            targets = [nt];
        else
            return [];
    }

    // rest unchanged — resolver.GetInterfaceImplementors works on any INamedTypeSymbol key
    // because SymbolEqualityComparer is used, and the source indexer walks type.AllInterfaces
    // which includes metadata interfaces. No additional work needed.
}
```

Verify: the source `SymbolResolver` constructor's `BuildInheritanceMaps` walks `type.AllInterfaces` — this enumerates both source and metadata interfaces, so `_interfaceImplementors` already keys metadata interfaces to source implementors. That's the intended path.

**Step 3: Run test → pass.**

**Step 4: Commit.**

```
git commit -m "feat: find_implementations accepts metadata interfaces (source implementors)"
```

---

## Task 5: Update SKILL.md with Tier-2 guidance

**Files:**
- Modify: [plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md](../../plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md)

**Step 1: Upgrade the per-tool metadata-behavior table rows** for `find_references`, `find_callers`, `find_implementations` from "No (Phase 2)" to "Yes — finds source call/reference/implementation sites for external symbols."

**Step 2: Update the decision tree**: replace the placeholder `"Who in my code uses it? → deferred to Phase 2"` with the real direction: `"Who in my code uses it? → find_references / find_callers / find_implementations with the external symbol's fully qualified name."`

**Step 3: Add a worked example.** "I want to know everything in my code that consumes `Microsoft.Extensions.DependencyInjection.IServiceCollection`" — show `find_references` call, note that returned items all have `origin.kind === "source"` (the references live in source) even though the *target* was a metadata symbol.

**Step 4: Commit.**

```
git commit -m "docs(skill): update external-assembly guidance for Tier-2 reference tools"
```

---

## Task 6: Full sweep and PR

**Step 1: Run everything.**

Run: `dotnet build && dotnet test`
Expected: Pass, zero warnings.

**Step 2: Verification.** Use @superpowers:verification-before-completion — exercise the server via MCP and call `find_references` / `find_callers` / `find_implementations` on known metadata symbols from your own .NET project.

**Step 3: PR titled `feat: external-assembly analysis — Phase 2 (Tier-2 references)`** linking issue #95 and the design doc.
