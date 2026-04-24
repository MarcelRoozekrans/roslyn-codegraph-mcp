# Performance Fixes & Missing Benchmarks — Design

**Date:** 2026-04-24
**Goal:** Fix two measurable code-level performance regressions introduced by the metadata support work (Phase 1-3), and add missing benchmarks for `inspect_external_assembly` and `peek_il`.

---

## Regressions being fixed

| Tool | Before | After | Root cause |
|------|-------:|------:|------------|
| `find_callers` | 406 µs | 611 µs (+50%) | `FindImplementationForInterfaceMember` called on every invocation without a name pre-filter |
| `find_attribute_usages` | 20 µs / 312 B | 33 µs / 848 B (+65%) | `ToDisplayString(MinimallyQualifiedFormat)` in hot result-building path |

## Accepted (test-solution growth, not regressions)

| Tool | Before | After | Reason |
|------|-------:|------:|--------|
| `get_diagnostics` | 98 µs | 194 µs (+98%) | Test fixture grew with Phase 1-3 NuGet packages and test files — `compilation.GetDiagnostics()` is O(code size) |
| `get_code_actions` | 765 µs | 1.1 ms (+44%) | Same test-solution growth |

No code changes for these two.

---

## Fix 1 — `find_callers`: name-filter before `FindImplementationForInterfaceMember`

**File:** `src/RoslynCodeLens/Tools/FindCallersLogic.cs`

**Problem:** `IsMethodMatch` is called in the inner loop for every `InvocationExpressionSyntax` in the solution. When the target is an interface method, it reaches `IsInterfaceImplementation`, which calls `containingType.FindImplementationForInterfaceMember(targetMethod)` — a Roslyn API that walks the type's interface map. This runs without any pre-filter, so even invocations to methods with completely different names trigger the type-hierarchy walk.

**Fix:** Add a name equality check in `IsMethodMatch` before delegating to `IsInterfaceImplementation`:

```csharp
// Fast name check before expensive interface implementation lookup
if (!string.Equals(calledMethod.Name, targetMethods[i].Name, StringComparison.Ordinal))
    continue;
if (IsInterfaceImplementation(calledMethod, targetMethods[i]))
    return true;
```

This single string comparison eliminates the `FindImplementationForInterfaceMember` call for the vast majority of invocations in the codebase.

---

## Fix 2 — `find_attribute_usages`: default display format in `BuildResults`

**File:** `src/RoslynCodeLens/Tools/FindAttributeUsagesLogic.cs`

**Problem:** `BuildResults` calls `ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)` for every non-type symbol. `MinimallyQualifiedFormat` is significantly more expensive than the default format and allocates more. The old code (pre-metadata work) used the default format throughout.

**Fix:**

```csharp
// Before
var targetName = symbol is INamedTypeSymbol
    ? symbol.ToDisplayString()
    : symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

// After
var targetName = symbol.ToDisplayString();
```

The default format is fully qualified for types and qualified-enough for members. The `MinimallyQualifiedFormat` was added for shorter output but the tool's consumers (AI agents) benefit more from fully qualified names.

---

## New benchmarks

**File:** `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

### `inspect_external_assembly`

Two benchmarks — summary mode (fast, returns namespace tree) and namespace mode (more expensive, returns type members):

```csharp
[Benchmark(Description = "inspect_external_assembly: summary mode")]
public object InspectExternalAssemblySummary()
{
    return InspectExternalAssemblyLogic.Execute(_metadata, "Microsoft.Extensions.DependencyInjection", "summary", null);
}

[Benchmark(Description = "inspect_external_assembly: namespace mode")]
public object InspectExternalAssemblyNamespace()
{
    return InspectExternalAssemblyLogic.Execute(_metadata, "Microsoft.Extensions.DependencyInjection", "namespace",
        "Microsoft.Extensions.DependencyInjection");
}
```

`Microsoft.Extensions.DependencyInjection` is already referenced by `TestLib2` in the test fixture.

### `peek_il`

`peek_il` requires an `IlDisassemblerAdapter` — add it to the benchmark setup alongside the existing `MetadataSymbolResolver`:

```csharp
private IlDisassemblerAdapter _ilDisassembler = null!;

// in GlobalSetup:
_ilDisassembler = new IlDisassemblerAdapter();
```

Target a concrete, non-abstract method from `Microsoft.Extensions.DependencyInjection`:

```csharp
[Benchmark(Description = "peek_il: ServiceCollectionServiceExtensions.AddScoped")]
public object PeekIl()
{
    return PeekIlLogic.Execute(_loaded, _metadata, _ilDisassembler,
        "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Type)");
}
```

---

## Key decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| `get_diagnostics` regression | Accept | Test-solution growth, logic unchanged |
| `get_code_actions` regression | Accept | Test-solution growth, logic unchanged |
| `find_callers` fix location | `IsMethodMatch` inner loop | Earliest safe exit point |
| `find_attribute_usages` format | Revert to default | Default is fully qualified; `MinimallyQualifiedFormat` gain was cosmetic |
| `inspect_external_assembly` benchmark target | `Microsoft.Extensions.DependencyInjection` | Already referenced by test fixture, well-known stable API |
| `peek_il` benchmark target | `ServiceCollectionServiceExtensions.AddScoped` | Concrete, non-abstract, stable signature |
