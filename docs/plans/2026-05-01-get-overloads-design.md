# `get_overloads` Design

**Status:** Approved 2026-05-01

## Goal

Return every overload of a method (or constructor) symbol — source AND metadata — with full signature/parameter/modifier detail in one call. Saves agents from calling `analyze_method` per overload to compare signatures side-by-side.

## Use cases

- "What overloads does `Console.WriteLine` have?" — 18+ BCL overloads in one call.
- "Which `Add` overload should I call here?" — agent inspects parameters/types/`params`/optional defaults across all overloads at once.
- "Show me all constructor variants" — `Greeter.Greeter` returns the ctor overload set.

## API

```csharp
GetOverloadsResult Execute(string symbol)
```

- `symbol` is a method or constructor name (e.g. `Greeter.Greet`, `Greeter.Greeter`, `System.Console.WriteLine`).

## Output

```csharp
public record GetOverloadsResult(
    string ContainingType,
    IReadOnlyList<OverloadInfo> Overloads);

public record OverloadInfo(
    string Signature,
    string ReturnType,
    IReadOnlyList<OverloadParameter> Parameters,
    string Accessibility,
    bool IsStatic,
    bool IsVirtual,
    bool IsAbstract,
    bool IsOverride,
    bool IsAsync,
    bool IsExtensionMethod,
    IReadOnlyList<string> TypeParameters,
    string? XmlDocSummary,
    string FilePath,
    int Line);

public record OverloadParameter(
    string Name,
    string Type,
    bool IsOptional,
    string? DefaultValue,
    bool IsParams,
    string Modifier);
```

`ContainingType`: the FQN of the containing type whose overload set was returned. Empty when the symbol can't be resolved.

`Modifier`: one of `""`, `"ref"`, `"out"`, `"in"`. (Roslyn's `RefKind`.)

`Accessibility`: lowercase string — `public`, `internal`, `protected`, `private`, `protected internal`, `private protected`.

`XmlDocSummary`: the inner text of the `<summary>` tag if the source method carries an XML doc comment; otherwise null. Metadata methods get null.

## Sort

`(Parameters.Count ASC, then Signature ordinal ASC)`. Stable, readable, matches IDE convention.

## Architecture

### Symbol resolution

1. Try `SymbolResolver.FindMethods(symbol)`. If non-empty, take `methods[0].ContainingType` and the method name (last segment of `symbol`).
2. Otherwise fall back to `MetadataSymbolResolver.Resolve(symbol)`. If `ResolvedSymbol.Symbol` is `IMethodSymbol`, take its `ContainingType` and name.
3. Constructor case: if the last segment of `symbol` equals the second-to-last segment (e.g. `Greeter.Greeter`), the method name is `.ctor` for `containingType.GetMembers(".ctor")`.
4. If neither resolves, return an empty result (`ContainingType: ""`, `Overloads: []`).

### Enumeration

`containingType.GetMembers(methodName).OfType<IMethodSymbol>()` returns every overload — source AND metadata, in declaration order. Filter out `IMethodSymbol` whose `MethodKind == UserDefinedOperator` or `Conversion` (operators are out of scope).

### Per-overload assembly

- `Signature`: `IMethodSymbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)` — produces `string Greet(string name)` style.
- `ReturnType`: `IMethodSymbol.ReturnType.ToDisplayString(NullableFlowState.NotApplicable)` with nullable annotations included.
- `Parameters`: walk `IMethodSymbol.Parameters`, capture name/type/modifier/default/`HasParamsModifier`.
  - `DefaultValue`: when `IParameterSymbol.HasExplicitDefaultValue`, render `ExplicitDefaultValue` as a literal string (`null`, `42`, `"x"`, etc.).
- Modifiers (`IsStatic`/`IsVirtual`/`IsAbstract`/`IsOverride`/`IsAsync`/`IsExtensionMethod`): straight from `IMethodSymbol`.
- `TypeParameters`: `IMethodSymbol.TypeParameters.Select(t => t.Name).ToList()`.
- `XmlDocSummary`: `IMethodSymbol.GetDocumentationCommentXml()` parsed for `<summary>` inner text, trimmed. Null if empty or absent.
- `FilePath` / `Line`: `Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan()`. Empty/0 for metadata-only.

### Sort + return

Sort the assembled list by `(Parameters.Count, Signature)` ordinal.

## Edge cases

| Case | Handling |
|---|---|
| Method on interface vs implementation | Returns overloads on whichever type matches the input. Agent calls again with the impl FQN if they want both. |
| Generic instantiation (`List<int>.Add`) | Resolves to `List<T>.Add` — overloads come from the unbound generic definition. |
| Extension method | `IsExtensionMethod: true`; `ContainingType` is the static class, not the receiver. |
| Operator overload (`+`, `==`) | Excluded — `MethodKind.UserDefinedOperator/Conversion` filter. |
| Constructor symbol (`Type.Type`) | `methodName` becomes `.ctor`; returns the constructor overload set. |
| Single overload | Returns a 1-element list. |
| Symbol not found | `ContainingType: ""`, `Overloads: []`. |
| Method with `[Obsolete]` | Reported normally — agent can use `find_obsolete_usage` for migration planning. |
| `params` array | `OverloadParameter.IsParams: true`; the array element type still in `Type`. |
| `ref readonly` parameter | `Modifier: "in"` (Roslyn folds `ref readonly` into `RefKind.In` for in-source). |

## Testing

Fixture: add `OverloadSamples.cs` to `TestLib/`:

```csharp
public class OverloadSamples
{
    /// <summary>Add two integers.</summary>
    public int Add(int a, int b) => a + b;

    /// <summary>Add a sequence of integers.</summary>
    public int Add(params int[] values) { /* ... */ return 0; }

    /// <summary>Add with a comparer.</summary>
    public TKey Add<TKey>(TKey a, TKey b, IComparer<TKey> comparer) => a;

    public string Echo(string s, int times = 1) => string.Concat(Enumerable.Repeat(s, times));

    public static OverloadSamples FromString(string s) => new();
    public static OverloadSamples FromString(string s, int multiplier) => new();
}

public static class OverloadExtensions
{
    public static int Doubled(this int x) => x * 2;
    public static int Doubled(this int x, int factor) => x * factor;
}
```

Tests:
- `Result_ReturnsAllOverloads` — `OverloadSamples.Add` returns 3 overloads.
- `Result_SortedByParameterCountThenSignature`.
- `Parameters_IncludeNamesAndTypes`.
- `Parameters_HasParamsFlag` — `Add(params int[])` reports `IsParams: true`.
- `Parameters_HasOptionalDefault` — `Echo(s, times = 1)` reports `IsOptional: true`, `DefaultValue: "1"`.
- `GenericMethod_TypeParametersPopulated` — `Add<TKey>` reports `TypeParameters: ["TKey"]`.
- `XmlDocSummary_PopulatedForDocumentedMethod` — `Add` overloads return `"Add two integers."` etc.
- `ExtensionMethod_FlagSet` — `OverloadExtensions.Doubled` reports `IsExtensionMethod: true`.
- `MetadataMethod_FindsAllBclOverloads` — `System.Console.WriteLine` returns ≥10 overloads.
- `Constructors_ReturnsCtorOverloads` — `Greeter.Greeter` returns ctor list.
- `UnknownSymbol_ReturnsEmpty`.
- `OperatorsExcluded` — a fixture type with a `+` operator: querying `Type.op_Addition` returns empty (or just doesn't include the operator).
- `StaticMethod_IsStaticFlagSet` — `OverloadSamples.FromString` reports `IsStatic: true`.

## Performance

`containingType.GetMembers(name)` is O(M) over members of the type — Roslyn caches this. No solution-wide walk.

Benchmark: `get_overloads: System.Console.WriteLine`. Should be sub-millisecond.

## MCP wrapper

Standard pattern:

```csharp
[McpServerToolType]
public static class GetOverloadsTool
{
    [McpServerTool(Name = "get_overloads")]
    [Description(...)]
    public static GetOverloadsResult Execute(MultiSolutionManager manager, string symbol)
    { ... }
}
```

Auto-registered via `WithToolsFromAssembly()`.

## Out of scope (deferred)

- Source-navigation links into metadata overloads — `peek_il` covers IL inspection separately.
- Cross-type overloads from different containing types — that's `find_implementations` territory.
- Overload-resolution-against-arguments — agent can call `find_callers` if it needs to see how each overload is actually invoked.
- Operator overloads — separate query story (deferred to a possible future `get_operators` tool).

## File checklist

- `src/RoslynCodeLens/Tools/GetOverloadsLogic.cs`
- `src/RoslynCodeLens/Tools/GetOverloadsTool.cs`
- `src/RoslynCodeLens/Models/GetOverloadsResult.cs`
- `src/RoslynCodeLens/Models/OverloadInfo.cs`
- `src/RoslynCodeLens/Models/OverloadParameter.cs`
- `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/OverloadSamples.cs`
- `tests/RoslynCodeLens.Tests/Tools/GetOverloadsToolTests.cs`
- `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs` — add benchmark
- `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` — Red Flags, Quick Reference, Navigating Code
- `README.md` — Features list
- `CLAUDE.md` — bump tool count
