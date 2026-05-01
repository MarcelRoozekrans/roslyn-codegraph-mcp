# `get_overloads` Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a new MCP tool `get_overloads` that returns every overload of a method or constructor symbol — source AND metadata — with full signature/parameter/modifier detail in one call.

**Architecture:** Resolve the symbol via `SymbolResolver.FindMethods` (source) or `MetadataSymbolResolver.Resolve` (metadata fallback). From the resolved method's `ContainingType`, enumerate `GetMembers(name).OfType<IMethodSymbol>()`. Filter out user-defined operators/conversions. Build per-overload info (signature, params, modifiers, XML doc, location). Sort by parameter count then signature.

**Tech Stack:** Roslyn (`Microsoft.CodeAnalysis`), xUnit, BenchmarkDotNet, ModelContextProtocol.Server.

**Reference design:** `docs/plans/2026-05-01-get-overloads-design.md`

**Patterns to mirror:**
- Source + metadata symbol resolution: `src/RoslynCodeLens/Tools/FindCallersLogic.cs:13-28`
- MCP wrapper / auto-registration: any tool in `src/RoslynCodeLens/Tools/`; `Program.cs:35` uses `WithToolsFromAssembly()` — no edit needed.

---

## Task 1: Models

**Files:**
- Create: `src/RoslynCodeLens/Models/OverloadParameter.cs`
- Create: `src/RoslynCodeLens/Models/OverloadInfo.cs`
- Create: `src/RoslynCodeLens/Models/GetOverloadsResult.cs`

**Step 1: `OverloadParameter.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record OverloadParameter(
    string Name,
    string Type,
    bool IsOptional,
    string? DefaultValue,
    bool IsParams,
    string Modifier);
```

**Step 2: `OverloadInfo.cs`**

```csharp
namespace RoslynCodeLens.Models;

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
```

**Step 3: `GetOverloadsResult.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record GetOverloadsResult(
    string ContainingType,
    IReadOnlyList<OverloadInfo> Overloads);
```

**Step 4: Build**

```bash
dotnet build src/RoslynCodeLens
```

Expected: 0 errors.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Models/OverloadParameter.cs \
  src/RoslynCodeLens/Models/OverloadInfo.cs \
  src/RoslynCodeLens/Models/GetOverloadsResult.cs
git commit -m "feat: add models for get_overloads"
```

---

## Task 2: Test fixture

**Files:**
- Create: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/OverloadSamples.cs`

**Step 1: Create the fixture**

```csharp
using System.Collections.Generic;
using System.Linq;

namespace TestLib.OverloadSamples;

public class OverloadSamples
{
    /// <summary>Add two integers.</summary>
    public int Add(int a, int b) => a + b;

    /// <summary>Add a sequence of integers.</summary>
    public int Add(params int[] values) => values.Sum();

    /// <summary>Add with a comparer.</summary>
    public TKey Add<TKey>(TKey a, TKey b, IComparer<TKey> comparer) => a;

    /// <summary>Echo a string with optional repeat count.</summary>
    public string Echo(string s, int times = 1)
        => string.Concat(Enumerable.Repeat(s, times));

    public static OverloadSamples FromString(string s) => new();
    public static OverloadSamples FromString(string s, int multiplier) => new();
}

public static class OverloadExtensions
{
    public static int Doubled(this int x) => x * 2;
    public static int Doubled(this int x, int factor) => x * factor;
}
```

**Step 2: Build**

```bash
dotnet build tests/RoslynCodeLens.Tests
```

Expected: 0 errors.

**Step 3: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/OverloadSamples.cs
git commit -m "test: add OverloadSamples fixture for get_overloads"
```

---

## Task 3: `GetOverloadsLogic` + tests (TDD)

**Files:**
- Create: `src/RoslynCodeLens/Tools/GetOverloadsLogic.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/GetOverloadsToolTests.cs`

**Step 1: Write the failing tests**

`tests/RoslynCodeLens.Tests/Tools/GetOverloadsToolTests.cs`:

```csharp
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GetOverloadsToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;
    private MetadataSymbolResolver _metadata = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _resolver = new SymbolResolver(_loaded);
        _metadata = new MetadataSymbolResolver(_loaded, _resolver);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Result_ReturnsAllOverloads()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Add");

        Assert.Contains("OverloadSamples", result.ContainingType, StringComparison.Ordinal);
        Assert.Equal(3, result.Overloads.Count);
    }

    [Fact]
    public void Result_SortedByParameterCountThenSignature()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Add");

        for (int i = 1; i < result.Overloads.Count; i++)
        {
            var prev = result.Overloads[i - 1];
            var curr = result.Overloads[i];
            if (prev.Parameters.Count == curr.Parameters.Count)
                Assert.True(string.CompareOrdinal(prev.Signature, curr.Signature) <= 0,
                    $"Sort violation: '{prev.Signature}' before '{curr.Signature}'");
            else
                Assert.True(prev.Parameters.Count < curr.Parameters.Count,
                    $"Sort violation by param count");
        }
    }

    [Fact]
    public void Parameters_IncludeNamesAndTypes()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Add");

        var twoParam = result.Overloads.Single(o => o.Parameters.Count == 2);
        Assert.Equal("a", twoParam.Parameters[0].Name);
        Assert.Equal("b", twoParam.Parameters[1].Name);
        Assert.Contains("int", twoParam.Parameters[0].Type, StringComparison.Ordinal);
    }

    [Fact]
    public void Parameters_HasParamsFlag()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Add");

        var paramsOverload = result.Overloads.Single(o =>
            o.Parameters.Count == 1 && o.Parameters[0].IsParams);
        Assert.Equal("values", paramsOverload.Parameters[0].Name);
    }

    [Fact]
    public void Parameters_HasOptionalDefault()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Echo");

        var echo = Assert.Single(result.Overloads);
        var times = echo.Parameters[1];
        Assert.Equal("times", times.Name);
        Assert.True(times.IsOptional);
        Assert.Equal("1", times.DefaultValue);
    }

    [Fact]
    public void GenericMethod_TypeParametersPopulated()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Add");

        var generic = result.Overloads.Single(o => o.TypeParameters.Count > 0);
        Assert.Contains("TKey", generic.TypeParameters);
    }

    [Fact]
    public void XmlDocSummary_PopulatedForDocumentedMethod()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Add");

        Assert.All(result.Overloads, o =>
        {
            Assert.NotNull(o.XmlDocSummary);
            Assert.NotEmpty(o.XmlDocSummary!);
        });
    }

    [Fact]
    public void ExtensionMethod_FlagSet()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadExtensions.Doubled");

        Assert.NotEmpty(result.Overloads);
        Assert.All(result.Overloads, o => Assert.True(o.IsExtensionMethod));
    }

    [Fact]
    public void StaticMethod_IsStaticFlagSet()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.FromString");

        Assert.Equal(2, result.Overloads.Count);
        Assert.All(result.Overloads, o => Assert.True(o.IsStatic));
    }

    [Fact]
    public void MetadataMethod_FindsAllBclOverloads()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "System.Console.WriteLine");

        // BCL Console.WriteLine has 18+ overloads; require at least 10 to allow for runtime variation.
        Assert.True(result.Overloads.Count >= 10,
            $"Expected >=10 Console.WriteLine overloads, got {result.Overloads.Count}");
    }

    [Fact]
    public void Constructors_ReturnsCtorOverloads()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "Greeter.Greeter");

        // Greeter has at least an implicit/declared constructor.
        Assert.NotEmpty(result.Overloads);
    }

    [Fact]
    public void UnknownSymbol_ReturnsEmpty()
    {
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "Does.Not.Exist");

        Assert.Empty(result.ContainingType);
        Assert.Empty(result.Overloads);
    }

    [Fact]
    public void OperatorsExcluded()
    {
        // OverloadSamples doesn't define operators; querying Add must not include any operator-kind methods.
        var result = GetOverloadsLogic.Execute(_resolver, _metadata, "OverloadSamples.Add");

        Assert.All(result.Overloads, o =>
            Assert.DoesNotContain("op_", o.Signature, StringComparison.Ordinal));
    }
}
```

**Step 2: Run failing**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetOverloadsToolTests"
```

Expect compile error (`GetOverloadsLogic` doesn't exist).

**Step 3: Create `src/RoslynCodeLens/Tools/GetOverloadsLogic.cs`**

```csharp
using System.Globalization;
using System.Xml;
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class GetOverloadsLogic
{
    private static readonly SymbolDisplayFormat SignatureFormat =
        SymbolDisplayFormat.CSharpShortErrorMessageFormat;

    public static GetOverloadsResult Execute(
        SymbolResolver resolver,
        MetadataSymbolResolver metadata,
        string symbol)
    {
        var (containingType, methodName) = ResolveContainingTypeAndName(resolver, metadata, symbol);
        if (containingType is null || string.IsNullOrEmpty(methodName))
            return new GetOverloadsResult(string.Empty, []);

        var overloads = containingType
            .GetMembers(methodName)
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind != MethodKind.UserDefinedOperator
                     && m.MethodKind != MethodKind.Conversion)
            .Select(BuildOverloadInfo)
            .ToList();

        overloads.Sort((a, b) =>
        {
            var byCount = a.Parameters.Count.CompareTo(b.Parameters.Count);
            if (byCount != 0) return byCount;
            return string.CompareOrdinal(a.Signature, b.Signature);
        });

        return new GetOverloadsResult(
            ContainingType: containingType.ToDisplayString(),
            Overloads: overloads);
    }

    private static (INamedTypeSymbol? Type, string Name) ResolveContainingTypeAndName(
        SymbolResolver resolver, MetadataSymbolResolver metadata, string symbol)
    {
        var parts = symbol.Split('.');
        if (parts.Length < 2) return (null, string.Empty);

        var lastSegment = parts[^1];
        var typeName = string.Join('.', parts[..^1]);
        var typeNameLastSegment = parts[^2];

        // Constructor case: Type.Type → resolve as ".ctor" on Type.
        var isConstructor = string.Equals(lastSegment, typeNameLastSegment, StringComparison.Ordinal);
        var methodName = isConstructor ? ".ctor" : lastSegment;

        // 1) Source path: SymbolResolver.FindMethods works for ordinary methods. For
        //    constructors, walk types directly because FindMethods uses the literal
        //    segment as the member name.
        if (!isConstructor)
        {
            var methods = resolver.FindMethods(symbol);
            if (methods.Count > 0)
                return (methods[0].ContainingType, methodName);
        }
        else
        {
            foreach (var type in resolver.FindNamedTypes(typeName))
                if (type.GetMembers(".ctor").OfType<IMethodSymbol>().Any())
                    return (type, methodName);
        }

        // 2) Metadata fallback.
        var resolved = metadata.Resolve(symbol);
        if (resolved?.Symbol is IMethodSymbol mm)
            return (mm.ContainingType, methodName);
        if (resolved?.Symbol is INamedTypeSymbol nt && isConstructor)
            return (nt, methodName);

        return (null, string.Empty);
    }

    private static OverloadInfo BuildOverloadInfo(IMethodSymbol method)
    {
        var location = method.Locations.FirstOrDefault(l => l.IsInSource);
        var (file, line) = location is not null
            ? (location.GetLineSpan().Path, location.GetLineSpan().StartLinePosition.Line + 1)
            : (string.Empty, 0);

        return new OverloadInfo(
            Signature: method.ToDisplayString(SignatureFormat),
            ReturnType: method.ReturnType.ToDisplayString(),
            Parameters: method.Parameters.Select(BuildParameter).ToList(),
            Accessibility: AccessibilityToString(method.DeclaredAccessibility),
            IsStatic: method.IsStatic,
            IsVirtual: method.IsVirtual,
            IsAbstract: method.IsAbstract,
            IsOverride: method.IsOverride,
            IsAsync: method.IsAsync,
            IsExtensionMethod: method.IsExtensionMethod,
            TypeParameters: method.TypeParameters.Select(t => t.Name).ToList(),
            XmlDocSummary: ExtractSummary(method),
            FilePath: file,
            Line: line);
    }

    private static OverloadParameter BuildParameter(IParameterSymbol p)
    {
        var defaultText = p.HasExplicitDefaultValue
            ? FormatDefault(p.ExplicitDefaultValue)
            : null;

        var modifier = p.RefKind switch
        {
            RefKind.Ref => "ref",
            RefKind.Out => "out",
            RefKind.In => "in",
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

    private static string FormatDefault(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        char c => $"'{c}'",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null",
    };

    private static string AccessibilityToString(Accessibility a) => a switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.Private => "private",
        _ => "internal",
    };

    private static string? ExtractSummary(IMethodSymbol method)
    {
        var xml = method.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var summary = doc.SelectSingleNode("//summary");
            var text = summary?.InnerText.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
```

**Step 4: Run tests**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetOverloadsToolTests" --no-build
```

If `--no-build` fails, run `dotnet build` first. Expect 13/13 pass.

**Common debugging:**
- If `MetadataMethod_FindsAllBclOverloads` fails: `MetadataSymbolResolver.Resolve("System.Console.WriteLine")` should return one of the overloads. Its `ContainingType` is `System.Console`, then `GetMembers("WriteLine")` returns all overloads.
- If `Constructors_ReturnsCtorOverloads` returns empty: ensure `isConstructor` branch uses `FindNamedTypes(typeName)` and reads `.ctor` members.
- If `Parameters_HasOptionalDefault` returns wrong default: `IParameterSymbol.ExplicitDefaultValue` for `int = 1` is `(int)1`, formatted via `IFormattable.ToString(null, CultureInfo.InvariantCulture)` as `"1"`.
- If XML doc summary is empty: ensure the fixture's csproj has `<GenerateDocumentationFile>true</GenerateDocumentationFile>` OR Roslyn's in-memory compilation captures the XML doc regardless. If the test fails, set the property in `TestLib.csproj`.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/GetOverloadsLogic.cs \
  tests/RoslynCodeLens.Tests/Tools/GetOverloadsToolTests.cs
git commit -m "feat: add GetOverloadsLogic with tests"
```

---

## Task 4: MCP wrapper

**Files:**
- Create: `src/RoslynCodeLens/Tools/GetOverloadsTool.cs`

**Step 1: Create the wrapper**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetOverloadsTool
{
    [McpServerTool(Name = "get_overloads")]
    [Description(
        "Return every overload of a method (or constructor) in one call — source AND metadata. " +
        "Each overload includes the full signature, parameter names/types/modifiers (ref/out/in/" +
        "params/optional with defaults), return type, accessibility, modifiers (static/virtual/" +
        "abstract/override/async/extension), generic type parameters, XML doc summary, and source " +
        "location (empty for metadata). " +
        "Pass 'Type.Method' for ordinary methods or 'Type.Type' for constructors. Operator " +
        "overloads are excluded — they have a separate query story. " +
        "Sort: parameter count ASC, then signature ordinal ASC.")]
    public static GetOverloadsResult Execute(
        MultiSolutionManager manager,
        [Description("Method or constructor symbol (e.g. 'Greeter.Greet', 'Greeter.Greeter', 'System.Console.WriteLine').")]
        string symbol)
    {
        manager.EnsureLoaded();
        return GetOverloadsLogic.Execute(
            manager.GetResolver(),
            manager.GetMetadataResolver(),
            symbol);
    }
}
```

**Step 2: Build**

```bash
dotnet build
```

Expected: 0 errors. Auto-registered via `Program.cs:35`.

**Step 3: Run targeted tests**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetOverloadsToolTests" --no-build
```

Expect 13/13 pass.

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Tools/GetOverloadsTool.cs
git commit -m "feat: register get_overloads MCP tool"
```

---

## Task 5: Add benchmark

**Files:**
- Modify: `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

**Step 1**: Find the existing `analyze_method` benchmark; add immediately after.

```csharp
[Benchmark(Description = "get_overloads: System.Console.WriteLine")]
public object GetOverloads()
{
    return GetOverloadsLogic.Execute(_resolver, _metadata, "System.Console.WriteLine");
}
```

**Step 2: Build benchmarks**

```bash
dotnet build benchmarks/RoslynCodeLens.Benchmarks -c Release
```

Expected: 0 errors.

**Step 3: Commit**

```bash
git add benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs
git commit -m "bench: add get_overloads benchmark"
```

---

## Task 6: Update SKILL.md, README, CLAUDE.md

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
- Modify: `README.md`
- Modify: `CLAUDE.md`

**Step 1: SKILL.md — Red Flags routing table**

Add near `analyze_method`:

```
| "What overloads does this method have?" / "Show me all signatures of Foo" / "Compare overloads side-by-side" | `get_overloads` |
```

**Step 2: SKILL.md — Quick Reference**

Add near `analyze_method`:

```
| `get_overloads` | "What overloads does this method have?" |
```

**Step 3: SKILL.md — Navigating Code section**

Add as a new bullet near `analyze_method`:

```
- `get_overloads` — Every overload of a method or constructor (source + metadata) with full parameter detail, modifiers, generic type params, XML doc summary, and location. One call instead of N analyze_method calls.
```

**Step 4: README.md Features list**

Add near `analyze_method`:

```
- **get_overloads** — Every overload of a method/constructor (source + metadata) with full parameter and modifier detail in one call
```

**Step 5: CLAUDE.md tool count**

Bump from 33 to 34.

**Step 6: Sanity check**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetOverloadsToolTests"
```

Expect 13/13 pass.

**Step 7: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md \
  README.md \
  CLAUDE.md
git commit -m "docs: announce get_overloads in SKILL.md, README, CLAUDE.md"
```

---

## Done

After Task 6 the branch should have ~8 commits (design + plan + 6 tasks). 13/13 tests green, benchmark compiling, tool auto-registered.
