# `get_operators` Design

**Status:** Approved 2026-05-04

## Goal

Return every user-defined operator and conversion operator on a type — source AND metadata — with kind classification, signature, parameters, and source location. Closes the gap left by `get_overloads`, which explicitly excludes operators.

## Use cases

- "What operators does `Vector2` have?" — agent gets `+`, `-`, `*`, `==`, `!=`, conversions, etc. in one call.
- "Does this type support equality comparison?" — agent checks for `==` / `!=` presence without grepping.
- "What conversions exist between `Money` and `decimal`?" — agent sees both `implicit` directions and `explicit` ones with source/target types.
- "Are checked variants defined?" — agent inspects `IsCheckedVariant` to verify .NET 7+ checked-arithmetic support.

## API

```csharp
GetOperatorsResult Execute(string symbol)
```

- `symbol` is a type name (e.g. `Vector2`, `MyApp.Money`, `Outer.Nested`, `System.Decimal`).
- Type-only input. No operator-name filtering on the server side; agents filter `Kind` client-side.

## Output

```csharp
public record GetOperatorsResult(
    string ContainingType,
    IReadOnlyList<OperatorInfo> Operators);

public record OperatorInfo(
    string Kind,                                  // "+", "==", "implicit", "explicit", etc.
    string Signature,                             // "public static Vector2 operator +(Vector2 a, Vector2 b)"
    string ReturnType,
    IReadOnlyList<OverloadParameter> Parameters,  // existing record, reused
    string Accessibility,
    bool IsCheckedVariant,                        // op_Checked* → true
    string? XmlDocSummary,
    string FilePath,
    int Line);
```

`ContainingType`: FQN of the resolved type. Empty when symbol can't be resolved.

`OverloadParameter`: imported as-is from `Models/OverloadParameter.cs` — same shape works for operator parameters.

`Accessibility`: lowercase string — `public`, `internal`, `protected`, `private`, `protected internal`, `private protected`. (Operators are typically `public static` but the field is included for completeness.)

`XmlDocSummary`: inner text of the `<summary>` tag if the source operator carries an XML doc comment; otherwise `null`. Metadata operators get `null`.

## Sort

`(Kind ordinal ASC, Parameters.Count ASC, Signature ordinal ASC)`. Groups all `+` together, then by arity (unary before binary), then alphabetically. Stable and grouped by the field most agents will scan first.

## Operator → `Kind` mapping

| Roslyn metadata name | `Kind` | Notes |
|---|---|---|
| `op_UnaryPlus` | `"+"` | Unary; arity tells them apart from binary `+`. |
| `op_UnaryNegation` | `"-"` | Unary. |
| `op_LogicalNot` | `"!"` | |
| `op_OnesComplement` | `"~"` | |
| `op_Increment` | `"++"` | |
| `op_Decrement` | `"--"` | |
| `op_True` | `"true"` | Boolean operators. |
| `op_False` | `"false"` | |
| `op_Addition` | `"+"` | Binary. |
| `op_Subtraction` | `"-"` | Binary. |
| `op_Multiply` | `"*"` | |
| `op_Division` | `"/"` | |
| `op_Modulus` | `"%"` | |
| `op_BitwiseAnd` | `"&"` | |
| `op_BitwiseOr` | `"\|"` | |
| `op_ExclusiveOr` | `"^"` | |
| `op_LeftShift` | `"<<"` | |
| `op_RightShift` | `">>"` | |
| `op_UnsignedRightShift` | `">>>"` | C# 11+. |
| `op_Equality` | `"=="` | |
| `op_Inequality` | `"!="` | |
| `op_LessThan` | `"<"` | |
| `op_LessThanOrEqual` | `"<="` | |
| `op_GreaterThan` | `">"` | |
| `op_GreaterThanOrEqual` | `">="` | |
| `op_Implicit` | `"implicit"` | `MethodKind.Conversion`. |
| `op_Explicit` | `"explicit"` | `MethodKind.Conversion`. |
| `op_Checked*` (e.g. `op_CheckedAddition`) | strip `Checked` prefix → underlying mapping | `IsCheckedVariant: true`. |

Implementation: static `Dictionary<string, string>` keyed on metadata name; one fallback for `op_Checked*` that strips the prefix and looks up again. Unrecognized `op_*` names (forward compatibility for future C# operator additions) fall back to the metadata name verbatim.

## Architecture

Mirrors `GetOverloadsLogic` with two divergences: **type-level resolution** (no method-name segment) and **inverted method-kind filter** (operators only).

### Symbol resolution

```
ResolveContainingType(symbol):
    1. resolver.FindNamedTypes(symbol) — first match wins.
       (Reuses the same helper get_overloads' constructor path uses.)
    2. Fallback: metadata.Resolve(symbol).
       Accept ResolvedSymbol.Symbol as INamedTypeSymbol.
    3. Return null on both misses.
```

No method-name parsing — input is a single type name. `Vector2`, `MyApp.Money`, `Outer.Nested` all work via existing resolver behavior.

### Enumeration

```csharp
containingType
    .GetMembers()
    .OfType<IMethodSymbol>()
    .Where(m => m.MethodKind == MethodKind.UserDefinedOperator
             || m.MethodKind == MethodKind.Conversion)
    .Select(BuildOperatorInfo)
    .OrderBy(...)
```

`GetMembers()` (no name argument) is needed because we want all operators regardless of CLR name. This is O(M) over members of the type — same complexity tier as `get_overloads`, no solution-wide walk.

### Per-operator assembly

```
Kind            ← KindFromMetadataName(method.MetadataName, out isChecked)
Signature       ← method.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)
ReturnType      ← method.ReturnType.ToDisplayString()
Parameters      ← method.Parameters.Select(MethodDisplayHelpers.BuildParameter).ToList()
Accessibility   ← MethodDisplayHelpers.AccessibilityToString(method.DeclaredAccessibility)
IsCheckedVariant ← isChecked (out from KindFromMetadataName)
XmlDocSummary   ← MethodDisplayHelpers.ExtractSummary(method)
FilePath / Line ← method.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan() (or empty/0)
```

### Shared helper extraction

`GetOverloadsLogic` currently owns three private helpers that `GetOperatorsLogic` would re-implement verbatim:

- `BuildParameter(IParameterSymbol)` → `OverloadParameter`
- `AccessibilityToString(Accessibility)` → `string`
- `ExtractSummary(IMethodSymbol)` → `string?`

**Refactor:** extract these to a new `Tools/MethodDisplayHelpers.cs` static class. Update `GetOverloadsLogic` to call through. This is a small, safe pre-step that the implementation plan should sequence first (one commit, zero behavior change, existing `GetOverloadsToolTests` catch regressions).

### KindFromMetadataName

```csharp
private static readonly Dictionary<string, string> OperatorMap = new(StringComparer.Ordinal)
{
    ["op_Addition"] = "+",
    ["op_Subtraction"] = "-",
    // ... full table above ...
    ["op_Implicit"] = "implicit",
    ["op_Explicit"] = "explicit",
};

private static string KindFromMetadataName(string metadataName, out bool isChecked)
{
    isChecked = false;
    if (OperatorMap.TryGetValue(metadataName, out var kind))
        return kind;

    // Checked variant: op_CheckedAddition → strip "Checked" → op_Addition → "+"
    if (metadataName.StartsWith("op_Checked", StringComparison.Ordinal))
    {
        var unchecked_ = "op_" + metadataName["op_Checked".Length..];
        if (OperatorMap.TryGetValue(unchecked_, out var baseKind))
        {
            isChecked = true;
            return baseKind;
        }
    }

    return metadataName; // forward-compat fallback
}
```

## Edge cases

| Case | Handling |
|---|---|
| Type has no operators | `ContainingType: "<resolved FQN>"`, `Operators: []`. Distinguishes "unknown type" (empty containing type) from "known type, no operators." |
| Type doesn't resolve | `ContainingType: ""`, `Operators: []`. Mirrors `get_overloads`. |
| Generic type (`Wrapper<T>`) | Resolves to unbound generic; operators come from the open generic definition. Caller passes `Wrapper` (not `Wrapper<int>`). Same convention as other tools. |
| Record type with synthesized `==`/`!=` | Compiler-generated `==`/`!=` on records are `IMethodSymbol` with `MethodKind.UserDefinedOperator` and `IsImplicitlyDeclared: true`. Include them — they are operators on the type from the caller's perspective. `FilePath` may be empty (no source location), `XmlDocSummary` null. |
| Conversion operator both directions | Two separate entries: `implicit Money(decimal)` and `implicit decimal(Money)`. Distinguished by signature/return type. |
| Checked variant alongside unchecked | Both appear; sort order keeps them adjacent (`Kind` is the same `"+"`, then secondary sort by `Parameters.Count` then `Signature` puts checked after unchecked because the `Signature` includes the keyword). |
| Operator in metadata-only type (`int`, `decimal`) | Resolves via `MetadataSymbolResolver`. BCL types have many operators — agent should expect ~20-30 results for primitives. |
| Nested type | Standard `Outer.Nested` resolution via `SymbolResolver.FindNamedTypes`. |
| Type with same name in multiple projects | First match wins (consistent with `get_overloads`); operators come from that single resolved type. Agent disambiguates by passing the FQN. |
| Inherited operators | Excluded. `GetMembers` returns declared-only members. Operators in C# don't participate in inheritance the way virtual methods do — `Type.GetMembers` is the right scope. Documented in tool description. |

## Testing

Fixture: add `OperatorSamples.cs` to `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/`:

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

(`record struct` gives synthesized `==`/`!=` for free, exercising the implicitly-declared path.)

Tests:
- `Result_ReturnsAllOperators` — `Money` returns ≥10 entries (binary, comparison, conversions, synthesized equality).
- `Result_ContainingTypeIsFullyQualified`.
- `Result_SortedByKindThenArityThenSignature`.
- `BinaryAddition_KindIsPlus`.
- `Conversion_KindIsImplicitOrExplicit` — implicit-to-decimal returns `Kind: "implicit"`; explicit-from-decimal returns `Kind: "explicit"`.
- `CheckedVariant_FlagSet` — `op_CheckedAddition` returns `Kind: "+"`, `IsCheckedVariant: true`.
- `SynthesizedRecordEquality_Included` — `Money` returns `==` and `!=` even though the user didn't write them.
- `Parameters_ReusesOverloadParameterShape` — modifier/`in`/etc. populated.
- `XmlDocSummary_PopulatedForDocumentedOperator` — `+` returns `"Add two amounts in the same currency."`.
- `TypeWithNoOperators_ReturnsEmptyOperatorsButPopulatedContainingType` — `NoOperators` → `ContainingType: "TestLib.NoOperators"`, `Operators: []`.
- `UnknownType_ReturnsEmpty` — `ContainingType: ""`, `Operators: []`.
- `MetadataType_FindsBclOperators` — `System.Decimal` returns ≥20 operators (sanity check on metadata path).
- `Refactor_GetOverloadsStillWorks` — pre-existing `GetOverloadsToolTests` continue to pass after the helper extraction (regression guard, no new test code — just don't break the existing suite).

## Performance

`GetMembers()` is cached by Roslyn at the `INamedTypeSymbol` level — O(M) over the type's members. No solution-wide walk, no semantic-model invocation outside symbol resolution. Sub-millisecond for typical types; ≤5ms for `System.Decimal` (hundreds of members to scan, ~30 to keep).

Benchmark: add `GetOperators_SystemDecimal` to `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs` alongside the existing `GetOverloads_ConsoleWriteLine` benchmark. Same harness, same measurement protocol.

## MCP wrapper

```csharp
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

Auto-registered via `WithToolsFromAssembly()`.

## Out of scope (deferred → BACKLOG.md)

- **Server-side filtering by kind** — agent filters client-side. Avoids a YAGNI parameter.
- **Inherited operators** — operators don't inherit in C#; `GetMembers` (declaration-only) is correct.
- **Indexers** — separate concern (`get_indexers` if ever needed).
- **Source-navigation links into metadata operators** — `peek_il` covers IL inspection.
- **Operator-resolution-against-arguments** — agent calls `find_callers` if needed.

## File checklist

- `src/RoslynCodeLens/Tools/GetOperatorsLogic.cs`
- `src/RoslynCodeLens/Tools/GetOperatorsTool.cs`
- `src/RoslynCodeLens/Tools/MethodDisplayHelpers.cs` (extracted from `GetOverloadsLogic`)
- `src/RoslynCodeLens/Tools/GetOverloadsLogic.cs` (refactor to call helpers)
- `src/RoslynCodeLens/Models/GetOperatorsResult.cs`
- `src/RoslynCodeLens/Models/OperatorInfo.cs`
- `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/OperatorSamples.cs`
- `tests/RoslynCodeLens.Tests/Tools/GetOperatorsToolTests.cs`
- `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs` — add benchmark
- `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` — Quick Reference + Navigating Code
- `README.md` — Features list
- `CLAUDE.md` — bump tool count
