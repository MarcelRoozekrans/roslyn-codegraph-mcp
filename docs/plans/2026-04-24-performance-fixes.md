# Performance Fixes & Missing Benchmarks Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix two code-level performance regressions in `find_callers` and `find_attribute_usages`, and add missing benchmarks for `inspect_external_assembly` and `peek_il`.

**Architecture:** Two surgical one-liner fixes in existing logic files (no new files, no API changes), plus new benchmark methods in the existing `CodeGraphBenchmarks.cs`. Each fix is verified by running the existing test suite — no new tests are needed because the regressions are in the hot path of correctness-tested logic.

**Tech Stack:** C# / Roslyn / BenchmarkDotNet 0.15.x / xUnit

---

### Task 1: Fix `find_callers` — name pre-filter before `FindImplementationForInterfaceMember`

**Files:**
- Modify: `src/RoslynCodeLens/Tools/FindCallersLogic.cs` (lines 106–112)

**Context:** `IsMethodMatch` is called for every `InvocationExpressionSyntax` in the whole solution. The last section of that method iterates `targetMethods` and calls `IsInterfaceImplementation` for each. `IsInterfaceImplementation` calls Roslyn's `FindImplementationForInterfaceMember` — a type-hierarchy walk — without first checking whether the method names even match. Adding a name equality check before the call eliminates ~99% of the expensive calls.

**Step 1: Open `FindCallersLogic.cs` and locate the for loop at the bottom of `IsMethodMatch` (around line 106):**

Current code:
```csharp
for (int i = 0; i < targetMethods.Count; i++)
{
    if (IsInterfaceImplementation(calledMethod, targetMethods[i]))
        return true;
}
```

**Step 2: Add the name pre-filter:**

Replace with:
```csharp
for (int i = 0; i < targetMethods.Count; i++)
{
    if (!string.Equals(calledMethod.Name, targetMethods[i].Name, StringComparison.Ordinal))
        continue;
    if (IsInterfaceImplementation(calledMethod, targetMethods[i]))
        return true;
}
```

**Step 3: Run the existing `FindCallersToolTests` to confirm correctness is preserved:**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindCallersToolTests" -v normal
```

Expected: all tests pass (`FindCallers_ForMethod_ReturnsCallSites`, `FindCallers_MetadataExtensionMethod_FindsSourceInvocations`).

**Step 4: Commit:**

```bash
git add src/RoslynCodeLens/Tools/FindCallersLogic.cs
git commit -m "perf: skip FindImplementationForInterfaceMember when method names differ"
```

---

### Task 2: Fix `find_attribute_usages` — revert to default `ToDisplayString()` in `BuildResults`

**Files:**
- Modify: `src/RoslynCodeLens/Tools/FindAttributeUsagesLogic.cs` (lines 76–79)

**Context:** `BuildResults` constructs the `targetName` for each attribute usage. The current code uses `SymbolDisplayFormat.MinimallyQualifiedFormat` for non-type symbols. That format is significantly more expensive and allocates more than the default. The default format is fully qualified, which is actually better for AI consumers. The `MinimallyQualifiedFormat` choice was added during the metadata work and wasn't in the original implementation.

**Step 1: Open `FindAttributeUsagesLogic.cs` and locate `BuildResults` (around line 76):**

Current code:
```csharp
var targetName = symbol is INamedTypeSymbol
    ? symbol.ToDisplayString()
    : symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
```

**Step 2: Replace with a single call using the default format:**

```csharp
var targetName = symbol.ToDisplayString();
```

**Step 3: Run the full `FindAttributeUsagesToolTests` suite to confirm all tests still pass:**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindAttributeUsagesToolTests" -v normal
```

Expected: all 5 tests pass — pay special attention to `FindAttributeUsages_Obsolete_FindsMarkedMember` (asserts `TargetName.Contains("OldGreet")`) and `FindAttributeUsages_FullyQualifiedMetadata_FindsUsage`.

**Step 4: Commit:**

```bash
git add src/RoslynCodeLens/Tools/FindAttributeUsagesLogic.cs
git commit -m "perf: use default ToDisplayString() in FindAttributeUsagesLogic.BuildResults"
```

---

### Task 3: Add `inspect_external_assembly` benchmarks

**Files:**
- Modify: `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

**Context:** `inspect_external_assembly` has two meaningfully different modes: `summary` (fast, returns namespace tree) and `namespace` (slower, returns type members for one namespace). Both should be benchmarked. The test fixture's `TestLib2` already references `Microsoft.Extensions.DependencyInjection`, making it the right target assembly — it's small, stable, and always present.

`InspectExternalAssemblyLogic.Execute` is in `RoslynCodeLens.Tools` (already imported) and takes `(MetadataSymbolResolver metadata, string assemblyName, string mode, string? namespaceFilter)`.

**Step 1: Add two benchmark methods after the existing `FindAttributeUsages` benchmark method in `CodeGraphBenchmarks.cs`:**

```csharp
[Benchmark(Description = "inspect_external_assembly: summary mode")]
public object InspectExternalAssemblySummary()
{
    return InspectExternalAssemblyLogic.Execute(
        _metadata, "Microsoft.Extensions.DependencyInjection", "summary", null);
}

[Benchmark(Description = "inspect_external_assembly: namespace mode")]
public object InspectExternalAssemblyNamespace()
{
    return InspectExternalAssemblyLogic.Execute(
        _metadata, "Microsoft.Extensions.DependencyInjection", "namespace",
        "Microsoft.Extensions.DependencyInjection");
}
```

**Step 2: Build to confirm it compiles:**

```bash
dotnet build benchmarks/RoslynCodeLens.Benchmarks -c Release
```

Expected: Build succeeded, 0 errors.

**Step 3: Commit:**

```bash
git add benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs
git commit -m "bench: add inspect_external_assembly benchmarks (summary + namespace modes)"
```

---

### Task 4: Add `peek_il` benchmark

**Files:**
- Modify: `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

**Context:** `PeekIlLogic.Execute` requires an `IlDisassemblerAdapter`, which wraps a `PEFileCache`. Neither is currently in the benchmark class. Add both as fields, initialise them in `GlobalSetup`, and add one benchmark method.

`IlDisassemblerAdapter` and `PEFileCache` live in `RoslynCodeLens.Metadata` — add a `using` for that namespace.

Target method: `ServiceCollectionServiceExtensions.AddScoped(IServiceCollection, Type)` — a concrete, non-abstract extension method in the referenced NuGet assembly. The fully-qualified signature with parameter types is required by `PeekIlLogic`.

**Step 1: Add `using RoslynCodeLens.Metadata;` at the top of `CodeGraphBenchmarks.cs` alongside the existing usings.**

**Step 2: Add two fields after the existing `private string _diSetupPath` field:**

```csharp
private PEFileCache _peFileCache = null!;
private IlDisassemblerAdapter _ilDisassembler = null!;
```

**Step 3: Initialise them at the end of `GlobalSetup`:**

```csharp
_peFileCache = new PEFileCache();
_ilDisassembler = new IlDisassemblerAdapter(_peFileCache);
```

**Step 4: Add the benchmark method after the `InspectExternalAssemblyNamespace` method added in Task 3:**

```csharp
[Benchmark(Description = "peek_il: ServiceCollectionServiceExtensions.AddScoped")]
public object PeekIl()
{
    return PeekIlLogic.Execute(
        _loaded, _metadata, _ilDisassembler,
        "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Type)");
}
```

**Step 5: Build to confirm it compiles:**

```bash
dotnet build benchmarks/RoslynCodeLens.Benchmarks -c Release
```

Expected: Build succeeded, 0 errors.

**Step 6: Commit:**

```bash
git add benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs
git commit -m "bench: add peek_il benchmark"
```

---

### Task 5: Full test run + verify build

**Step 1: Run the full test suite:**

```bash
dotnet test tests/RoslynCodeLens.Tests -v normal
```

Expected: all tests pass, 0 failures.

**Step 2: Build the full solution:**

```bash
dotnet build
```

Expected: 0 errors.
