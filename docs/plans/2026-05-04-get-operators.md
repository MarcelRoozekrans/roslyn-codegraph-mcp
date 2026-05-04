# `get_operators` Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a new MCP tool `get_operators` that returns every user-defined operator and conversion operator on a type — source AND metadata — with kind classification, signature, parameters, and source location.

**Architecture:** Resolve the type via `SymbolResolver.FindNamedTypes` (source) or `MetadataSymbolResolver.Resolve` (metadata fallback). From the resolved `INamedTypeSymbol`, enumerate `GetMembers().OfType<IMethodSymbol>()` filtered to `MethodKind.UserDefinedOperator | MethodKind.Conversion`. Map each operator's metadata name (`op_Addition`, `op_Implicit`, `op_CheckedAddition`, …) to its C# kind (`+`, `implicit`, `+` with `IsCheckedVariant: true`). Mirrors `GetOverloadsLogic`; pre-step extracts shared parameter/accessibility/xml-doc helpers so both tools call through one implementation.

**Tech Stack:** Roslyn (`Microsoft.CodeAnalysis`), xUnit, BenchmarkDotNet, ModelContextProtocol.Server.

**Reference design:** `docs/plans/2026-05-04-get-operators-design.md`

**Patterns to mirror:**
- Symbol resolution + per-member assembly: `src/RoslynCodeLens/Tools/GetOverloadsLogic.cs`
- New shared-fixture test pattern: `tests/RoslynCodeLens.Tests/Tools/AnalyzeControlFlowToolTests.cs:7-20` (use `[Collection("TestSolution")]` + ctor injection of `TestSolutionFixture`).
- MCP wrapper / auto-registration: any tool in `src/RoslynCodeLens/Tools/`; `Program.cs` uses `WithToolsFromAssembly()` — no edit needed.

**Commit cadence:** one commit per task. Verify build + tests before each commit.

---

## Task 1: Refactor — extract `MethodDisplayHelpers` (zero behavior change)

**Why first:** `GetOperatorsLogic` will reuse three helpers currently private to `GetOverloadsLogic` (`BuildParameter`, `AccessibilityToString`, `ExtractSummary`). Extracting them now keeps the new tool DRY without later churn. Behavior must not change — existing `GetOverloadsToolTests` is the regression guard.

**Files:**
- Create: `src/RoslynCodeLens/Tools/MethodDisplayHelpers.cs`
- Modify: `src/RoslynCodeLens/Tools/GetOverloadsLogic.cs` (remove the three helpers, call through to new class)

**Step 1: Create `MethodDisplayHelpers.cs`**

Copy the three helpers verbatim from `GetOverloadsLogic.cs:116-196`. Make them `internal static` so the assembly's other tool classes can call them; keep `FormatDefault` as `private` inside the new class (it's an implementation detail of `BuildParameter`).

```csharp
using System.Globalization;
using System.Xml;
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

internal static class MethodDisplayHelpers
{
    internal static OverloadParameter BuildParameter(IParameterSymbol p)
    {
        var defaultText = p.HasExplicitDefaultValue
            ? FormatDefault(p.ExplicitDefaultValue, p.Type)
            : null;

        var modifier = p.RefKind switch
        {
            RefKind.Ref => "ref",
            RefKind.Out => "out",
            RefKind.In => "in",
            RefKind.RefReadOnlyParameter => "ref readonly",
            _ => string.Empty,
        };

        return new OverloadParameter(
            Name: p.Name,
            Type: p.Type.ToDisplayString(),
            IsOptional: p.IsOptional,
            DefaultValue: defaultText,
            IsParams: p.IsParams,
            Modifier: modifier);
    }

    internal static string AccessibilityToString(Accessibility a) => a switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.Private => "private",
        _ => "internal",
    };

    internal static string? ExtractSummary(IMethodSymbol method)
    {
        var xml = method.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var summary = doc.SelectSingleNode("//summary");
            var text = summary?.InnerText.Trim();
            if (string.IsNullOrEmpty(text)) return null;

            return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string FormatDefault(object? value, ITypeSymbol type)
    {
        if (value is not null && type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
        {
            foreach (var member in enumType.GetMembers().OfType<IFieldSymbol>())
            {
                if (member.HasConstantValue && Equals(member.ConstantValue, value))
                    return $"{enumType.Name}.{member.Name}";
            }
        }

        return value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            bool b => b ? "true" : "false",
            char c => $"'{c}'",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "null",
        };
    }
}
```

**Step 2: Update `GetOverloadsLogic.cs`**

Remove the three helpers (`BuildParameter`, `AccessibilityToString`, `ExtractSummary`) and `FormatDefault`. Update the two call sites in `BuildOverloadInfo`:

```csharp
// Before:
Parameters: method.Parameters.Select(BuildParameter).ToList(),
Accessibility: AccessibilityToString(method.DeclaredAccessibility),
// ...
XmlDocSummary: ExtractSummary(method),

// After:
Parameters: method.Parameters.Select(MethodDisplayHelpers.BuildParameter).ToList(),
Accessibility: MethodDisplayHelpers.AccessibilityToString(method.DeclaredAccessibility),
// ...
XmlDocSummary: MethodDisplayHelpers.ExtractSummary(method),
```

Remove the `using System.Globalization;` and `using System.Xml;` if no longer needed.

**Step 3: Build**

```bash
dotnet build
```

Expected: 0 errors, 0 warnings.

**Step 4: Run existing test suite (regression guard)**

```bash
dotnet test --filter "FullyQualifiedName~GetOverloadsToolTests"
```

Expected: all tests pass (same count as before refactor).

Then run the full suite to confirm no other tool was depending on these as `private` (they weren't — they're brand new private members — but verify):

```bash
dotnet test
```

Expected: 0 failures.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/MethodDisplayHelpers.cs src/RoslynCodeLens/Tools/GetOverloadsLogic.cs
git commit -m "refactor: extract MethodDisplayHelpers from GetOverloadsLogic"
```

---

## Task 2: Models

**Files:**
- Create: `src/RoslynCodeLens/Models/OperatorInfo.cs`
- Create: `src/RoslynCodeLens/Models/GetOperatorsResult.cs`

(Reuses existing `Models/OverloadParameter.cs` — no changes there.)

**Step 1: `OperatorInfo.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record OperatorInfo(
    string Kind,
    string Signature,
    string ReturnType,
    IReadOnlyList<OverloadParameter> Parameters,
    string Accessibility,
    bool IsCheckedVariant,
    string? XmlDocSummary,
    string FilePath,
    int Line);
```

**Step 2: `GetOperatorsResult.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record GetOperatorsResult(
    string ContainingType,
    IReadOnlyList<OperatorInfo> Operators);
```

**Step 3: Build**

```bash
dotnet build src/RoslynCodeLens
```

Expected: 0 errors.

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Models/OperatorInfo.cs src/RoslynCodeLens/Models/GetOperatorsResult.cs
git commit -m "feat: add models for get_operators"
```

---

## Task 3: Test fixture — `OperatorSamples.cs`

**Files:**
- Create: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/OperatorSamples.cs`

**Step 1: Create the fixture**

```csharp
namespace TestLib;

public readonly record struct Money(decimal Amount, string Currency)
{
    /// <summary>Add two amounts in the same currency.</summary>
    public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount, a.Currency);

    public static Money operator -(Money a, Money b) => new(a.Amount - b.Amount, a.Currency);

    public static Money operator *(Money a, decimal factor) => new(a.Amount * factor, a.Currency);

    public static bool operator <(Money a, Money b) => a.Amount < b.Amount;
    public static bool operator >(Money a, Money b) => a.Amount > b.Amount;
    public static bool operator <=(Money a, Money b) => a.Amount <= b.Amount;
    public static bool operator >=(Money a, Money b) => a.Amount >= b.Amount;

    public static implicit operator decimal(Money m) => m.Amount;
    public static explicit operator Money(decimal d) => new(d, "USD");

    // .NET 7+ checked variant
    public static Money operator checked +(Money a, Money b) => checked(new(a.Amount + b.Amount, a.Currency));
}

public class NoOperators { public int X { get; set; } }
```

`record struct` gives the synthesized `==`/`!=` operators for free, exercising the implicitly-declared path. `Money` overloads `==`/`!=` because `record` types do — those will appear with `IsImplicitlyDeclared: true` in addition to the `<`/`>`/etc. operators we wrote.

**Step 2: Build the test project**

```bash
dotnet build tests/RoslynCodeLens.Tests
```

Expected: 0 errors. (TestLib is a project reference in the test solution; building the test project triggers TestLib build.)

**Step 3: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/OperatorSamples.cs
git commit -m "test: add OperatorSamples fixture for get_operators"
```

---

## Task 4: `GetOperatorsLogic` skeleton + first failing test (TDD red)

**Files:**
- Create: `src/RoslynCodeLens/Tools/GetOperatorsLogic.cs` (skeleton only — returns empty)
- Create: `tests/RoslynCodeLens.Tests/Tools/GetOperatorsToolTests.cs`

**Step 1: Skeleton `GetOperatorsLogic.cs`**

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class GetOperatorsLogic
{
    public static GetOperatorsResult Execute(
        SymbolResolver resolver,
        MetadataSymbolResolver metadata,
        string symbol)
    {
        return new GetOperatorsResult(string.Empty, []);
    }
}
```

This compiles but always returns empty. The next steps drive the real implementation via failing tests.

**Step 2: Write the first failing test**

`tests/RoslynCodeLens.Tests/Tools/GetOperatorsToolTests.cs`:

```csharp
using RoslynCodeLens.Models;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetOperatorsToolTests
{
    private readonly TestSolutionFixture _fixture;

    public GetOperatorsToolTests(TestSolutionFixture fixture) => _fixture = fixture;

    [Fact]
    public void Result_ReturnsAllOperators()
    {
        var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");

        Assert.Contains("Money", result.ContainingType, StringComparison.Ordinal);
        // Money has: +, -, *, <, >, <=, >=, implicit decimal, explicit Money,
        // checked +, plus synthesized == and != from record struct = at least 12.
        Assert.True(result.Operators.Count >= 10,
            $"Expected ≥10 operators on Money, got {result.Operators.Count}");
    }
}
```

**Step 3: Build + run — verify it fails**

```bash
dotnet build
dotnet test --filter "FullyQualifiedName~GetOperatorsToolTests.Result_ReturnsAllOperators"
```

Expected: FAIL — `Operators.Count` is 0 (skeleton returns empty), assertion message shows `Expected ≥10 operators on Money, got 0`.

**Step 4: Commit (red state)**

```bash
git add src/RoslynCodeLens/Tools/GetOperatorsLogic.cs tests/RoslynCodeLens.Tests/Tools/GetOperatorsToolTests.cs
git commit -m "test: failing GetOperatorsLogic skeleton + first test"
```

---

## Task 5: `GetOperatorsLogic` — symbol resolution + enumeration (TDD green)

**Files:**
- Modify: `src/RoslynCodeLens/Tools/GetOperatorsLogic.cs`

**Step 1: Add `OperatorMap` constant + `KindFromMetadataName` helper**

At the top of the class:

```csharp
private static readonly Dictionary<string, string> OperatorMap = new(StringComparer.Ordinal)
{
    ["op_UnaryPlus"] = "+",
    ["op_UnaryNegation"] = "-",
    ["op_LogicalNot"] = "!",
    ["op_OnesComplement"] = "~",
    ["op_Increment"] = "++",
    ["op_Decrement"] = "--",
    ["op_True"] = "true",
    ["op_False"] = "false",
    ["op_Addition"] = "+",
    ["op_Subtraction"] = "-",
    ["op_Multiply"] = "*",
    ["op_Division"] = "/",
    ["op_Modulus"] = "%",
    ["op_BitwiseAnd"] = "&",
    ["op_BitwiseOr"] = "|",
    ["op_ExclusiveOr"] = "^",
    ["op_LeftShift"] = "<<",
    ["op_RightShift"] = ">>",
    ["op_UnsignedRightShift"] = ">>>",
    ["op_Equality"] = "==",
    ["op_Inequality"] = "!=",
    ["op_LessThan"] = "<",
    ["op_LessThanOrEqual"] = "<=",
    ["op_GreaterThan"] = ">",
    ["op_GreaterThanOrEqual"] = ">=",
    ["op_Implicit"] = "implicit",
    ["op_Explicit"] = "explicit",
};

private static string KindFromMetadataName(string metadataName, out bool isChecked)
{
    isChecked = false;
    if (OperatorMap.TryGetValue(metadataName, out var kind))
        return kind;

    if (metadataName.StartsWith("op_Checked", StringComparison.Ordinal))
    {
        var unchecked_ = "op_" + metadataName["op_Checked".Length..];
        if (OperatorMap.TryGetValue(unchecked_, out var baseKind))
        {
            isChecked = true;
            return baseKind;
        }
    }

    return metadataName;
}
```

**Step 2: Add `ResolveContainingType`**

```csharp
private static INamedTypeSymbol? ResolveContainingType(
    SymbolResolver resolver, MetadataSymbolResolver metadata, string symbol)
{
    var sourceTypes = resolver.FindNamedTypes(symbol);
    if (sourceTypes.Count > 0)
        return sourceTypes[0];

    var resolved = metadata.Resolve(symbol);
    if (resolved?.Symbol is INamedTypeSymbol nt)
        return nt;

    return null;
}
```

**Step 3: Add `BuildOperatorInfo`**

```csharp
private static readonly SymbolDisplayFormat SignatureFormat =
    SymbolDisplayFormat.CSharpShortErrorMessageFormat;

private static OperatorInfo BuildOperatorInfo(IMethodSymbol method)
{
    var location = method.Locations.FirstOrDefault(l => l.IsInSource);
    var (file, line) = (string.Empty, 0);
    if (location is not null)
    {
        var span = location.GetLineSpan();
        (file, line) = (span.Path, span.StartLinePosition.Line + 1);
    }

    var kind = KindFromMetadataName(method.MetadataName, out var isChecked);

    return new OperatorInfo(
        Kind: kind,
        Signature: method.ToDisplayString(SignatureFormat),
        ReturnType: method.ReturnType.ToDisplayString(),
        Parameters: method.Parameters.Select(MethodDisplayHelpers.BuildParameter).ToList(),
        Accessibility: MethodDisplayHelpers.AccessibilityToString(method.DeclaredAccessibility),
        IsCheckedVariant: isChecked,
        XmlDocSummary: MethodDisplayHelpers.ExtractSummary(method),
        FilePath: file,
        Line: line);
}
```

**Step 4: Replace the skeleton `Execute` body**

```csharp
public static GetOperatorsResult Execute(
    SymbolResolver resolver,
    MetadataSymbolResolver metadata,
    string symbol)
{
    var containingType = ResolveContainingType(resolver, metadata, symbol);
    if (containingType is null)
        return new GetOperatorsResult(string.Empty, []);

    var operators = containingType
        .GetMembers()
        .OfType<IMethodSymbol>()
        .Where(m => m.MethodKind == MethodKind.UserDefinedOperator
                 || m.MethodKind == MethodKind.Conversion)
        .Select(BuildOperatorInfo)
        .ToList();

    operators.Sort((a, b) =>
    {
        var byKind = string.CompareOrdinal(a.Kind, b.Kind);
        if (byKind != 0) return byKind;
        var byArity = a.Parameters.Count.CompareTo(b.Parameters.Count);
        if (byArity != 0) return byArity;
        return string.CompareOrdinal(a.Signature, b.Signature);
    });

    return new GetOperatorsResult(
        ContainingType: containingType.ToDisplayString(),
        Operators: operators);
}
```

**Step 5: Build + run the failing test**

```bash
dotnet build
dotnet test --filter "FullyQualifiedName~GetOperatorsToolTests.Result_ReturnsAllOperators"
```

Expected: PASS (Money has ≥10 operators).

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Tools/GetOperatorsLogic.cs
git commit -m "feat: implement GetOperatorsLogic with operator-kind mapping"
```

---

## Task 6: Add the remaining tests (TDD coverage)

**Files:**
- Modify: `tests/RoslynCodeLens.Tests/Tools/GetOperatorsToolTests.cs`

Add the following tests one at a time. After each addition, run `dotnet test --filter "FullyQualifiedName~GetOperatorsToolTests"` and verify PASS before moving to the next.

**Step 1: `Result_ContainingTypeIsFullyQualified`**

```csharp
[Fact]
public void Result_ContainingTypeIsFullyQualified()
{
    var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");
    Assert.Equal("TestLib.Money", result.ContainingType);
}
```

**Step 2: `Result_SortedByKindThenArityThenSignature`**

```csharp
[Fact]
public void Result_SortedByKindThenArityThenSignature()
{
    var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");

    for (int i = 1; i < result.Operators.Count; i++)
    {
        var prev = result.Operators[i - 1];
        var curr = result.Operators[i];
        var kindCmp = string.CompareOrdinal(prev.Kind, curr.Kind);
        if (kindCmp == 0)
        {
            var arityCmp = prev.Parameters.Count.CompareTo(curr.Parameters.Count);
            if (arityCmp == 0)
                Assert.True(string.CompareOrdinal(prev.Signature, curr.Signature) <= 0);
            else
                Assert.True(arityCmp < 0);
        }
        else
        {
            Assert.True(kindCmp < 0);
        }
    }
}
```

**Step 3: `BinaryAddition_KindIsPlus`**

```csharp
[Fact]
public void BinaryAddition_KindIsPlus()
{
    var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");
    Assert.Contains(result.Operators, op => op.Kind == "+" && !op.IsCheckedVariant);
}
```

**Step 4: `Conversion_KindIsImplicitOrExplicit`**

```csharp
[Fact]
public void Conversion_KindIsImplicitOrExplicit()
{
    var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");
    Assert.Contains(result.Operators, op => op.Kind == "implicit");
    Assert.Contains(result.Operators, op => op.Kind == "explicit");
}
```

**Step 5: `CheckedVariant_FlagSet`**

```csharp
[Fact]
public void CheckedVariant_FlagSet()
{
    var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");
    Assert.Contains(result.Operators, op => op.Kind == "+" && op.IsCheckedVariant);
}
```

**Step 6: `SynthesizedRecordEquality_Included`**

```csharp
[Fact]
public void SynthesizedRecordEquality_Included()
{
    var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");
    Assert.Contains(result.Operators, op => op.Kind == "==");
    Assert.Contains(result.Operators, op => op.Kind == "!=");
}
```

**Step 7: `Parameters_PopulatedWithTypeAndName`**

```csharp
[Fact]
public void Parameters_PopulatedWithTypeAndName()
{
    var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");
    var addition = result.Operators.First(op => op.Kind == "+" && op.Parameters.Count == 2 && !op.IsCheckedVariant);

    Assert.Equal(2, addition.Parameters.Count);
    Assert.All(addition.Parameters, p =>
    {
        Assert.False(string.IsNullOrEmpty(p.Name));
        Assert.False(string.IsNullOrEmpty(p.Type));
    });
}
```

**Step 8: `XmlDocSummary_PopulatedForDocumentedOperator`**

```csharp
[Fact]
public void XmlDocSummary_PopulatedForDocumentedOperator()
{
    var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "Money");
    var addition = result.Operators.FirstOrDefault(op =>
        op.Kind == "+" && op.Parameters.Count == 2 && !op.IsCheckedVariant);
    Assert.NotNull(addition);
    Assert.Equal("Add two amounts in the same currency.", addition!.XmlDocSummary);
}
```

**Step 9: `TypeWithNoOperators_ReturnsEmptyOperatorsButPopulatedContainingType`**

```csharp
[Fact]
public void TypeWithNoOperators_ReturnsEmptyOperatorsButPopulatedContainingType()
{
    var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "NoOperators");
    Assert.Equal("TestLib.NoOperators", result.ContainingType);
    Assert.Empty(result.Operators);
}
```

**Step 10: `UnknownType_ReturnsEmpty`**

```csharp
[Fact]
public void UnknownType_ReturnsEmpty()
{
    var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "TotallyMadeUpType");
    Assert.Equal(string.Empty, result.ContainingType);
    Assert.Empty(result.Operators);
}
```

**Step 11: `MetadataType_FindsBclOperators`**

```csharp
[Fact]
public void MetadataType_FindsBclOperators()
{
    var result = GetOperatorsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "System.Decimal");
    Assert.Contains("Decimal", result.ContainingType, StringComparison.Ordinal);
    Assert.True(result.Operators.Count >= 20,
        $"Expected ≥20 operators on System.Decimal, got {result.Operators.Count}");
    Assert.Contains(result.Operators, op => op.Kind == "+");
    Assert.Contains(result.Operators, op => op.Kind == "implicit");
}
```

**Step 12: Run full test class once**

```bash
dotnet test --filter "FullyQualifiedName~GetOperatorsToolTests"
```

Expected: 12 tests pass.

**Step 13: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Tools/GetOperatorsToolTests.cs
git commit -m "test: add coverage for GetOperatorsLogic"
```

---

## Task 7: MCP wrapper — `GetOperatorsTool`

**Files:**
- Create: `src/RoslynCodeLens/Tools/GetOperatorsTool.cs`

**Step 1: Create the tool wrapper**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetOperatorsTool
{
    [McpServerTool(Name = "get_operators")]
    [Description("List user-defined and conversion operators on a type. Returns each operator's kind, signature, parameters, return type, and source location. Includes compiler-synthesized record equality operators.")]
    public static GetOperatorsResult Execute(
        MultiSolutionManager manager,
        [Description("Type name (simple or fully qualified) — e.g. Vector2, MyApp.Money")] string symbol)
    {
        manager.EnsureLoaded();
        return GetOperatorsLogic.Execute(manager.GetResolver(), manager.GetMetadataResolver(), symbol);
    }
}
```

**Step 2: Build**

```bash
dotnet build
```

Expected: 0 errors, 0 warnings.

**Step 3: Run the full test suite (smoke check that nothing else broke)**

```bash
dotnet test
```

Expected: 0 failures.

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Tools/GetOperatorsTool.cs
git commit -m "feat: register get_operators MCP tool"
```

---

## Task 8: Benchmark

**Files:**
- Modify: `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

**Step 1: Find the existing `GetOverloads_ConsoleWriteLine` benchmark**

```bash
grep -n "GetOverloads_ConsoleWriteLine" benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs
```

**Step 2: Add a sibling benchmark**

Place this method directly below `GetOverloads_ConsoleWriteLine` so related benchmarks stay grouped:

```csharp
[Benchmark]
public GetOperatorsResult GetOperators_SystemDecimal()
    => GetOperatorsLogic.Execute(_resolver, _metadata, "System.Decimal");
```

(Field names `_resolver` / `_metadata` follow the same pattern as the existing get_overloads benchmark — verify by reading the file's `[GlobalSetup]` to confirm field names; adjust if they differ.)

**Step 3: Build the benchmark project**

```bash
dotnet build benchmarks/RoslynCodeLens.Benchmarks -c Release
```

Expected: 0 errors.

**Step 4: Optional — smoke-run the benchmark**

```bash
dotnet run --project benchmarks/RoslynCodeLens.Benchmarks -c Release -- --filter "*GetOperators*"
```

Expected: completes in under a minute, reports a sub-millisecond mean. This is informational only — do not block the commit on the result.

**Step 5: Commit**

```bash
git add benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs
git commit -m "bench: add get_operators benchmark"
```

---

## Task 9: Documentation

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
- Modify: `README.md`
- Modify: `CLAUDE.md`

**Step 1: SKILL.md — Quick Reference + Navigating Code sections**

Find the existing `get_overloads` entries and add a `get_operators` entry directly below each:

- In the **Quick Reference** table, add a row:
  ```
  | `get_operators` | List user-defined operators and conversions on a type | `Type name` |
  ```
- In the **Navigating Code** section, add a bullet:
  ```
  - **`get_operators`** — every `+`, `-`, `==`, `<`, conversion, etc. on a type, with kind, signature, source location. Includes synthesized record equality. Covers what `get_overloads` excludes.
  ```

(Exact section names may differ — open the file and place the entries in the analogous locations to `get_overloads`.)

**Step 2: README.md — Features list**

Find the bulleted Features list with the existing `get_overloads` entry. Add directly below:

```
- **`get_operators`** — list user-defined and conversion operators on a type
```

**Step 3: CLAUDE.md — bump tool count**

The line currently reads (approximate — confirm):

```
The `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` skill teaches Claude when and how to use the 34 code intelligence tools.
```

Bump `34` to `35`.

**Step 4: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md README.md CLAUDE.md
git commit -m "docs: announce get_operators in SKILL.md, README, CLAUDE.md"
```

---

## Task 10: BACKLOG cleanup

**Files:**
- Modify: `docs/BACKLOG.md`

**Step 1: Move `get_operators` out of active backlog**

Delete the `get_operators` line from section "2. Navigation niceties." Add a new row to the "Recently shipped" table:

```
| `get_operators` | Navigation | (PR # filled in after merge) |
```

(Leave the PR number as a placeholder — fill in via a follow-up edit when the PR lands, matching the convention of existing entries.)

**Step 2: Commit**

```bash
git add docs/BACKLOG.md
git commit -m "docs: mark get_operators as shipped in BACKLOG"
```

---

## Final verification

```bash
dotnet build
dotnet test
```

Expected: 0 errors, all tests pass (existing suite + 12 new `GetOperatorsToolTests` + the regression-guarded `GetOverloadsToolTests` from Task 1).

```bash
git log --oneline main..HEAD
```

Expected: 9 commits in this order:
1. `refactor: extract MethodDisplayHelpers from GetOverloadsLogic`
2. `feat: add models for get_operators`
3. `test: add OperatorSamples fixture for get_operators`
4. `test: failing GetOperatorsLogic skeleton + first test`
5. `feat: implement GetOperatorsLogic with operator-kind mapping`
6. `test: add coverage for GetOperatorsLogic`
7. `feat: register get_operators MCP tool`
8. `bench: add get_operators benchmark`
9. `docs: announce get_operators in SKILL.md, README, CLAUDE.md`
10. `docs: mark get_operators as shipped in BACKLOG`

Branch is ready for PR.
