# `find_obsolete_usage` Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a new MCP tool `find_obsolete_usage` that finds every call site referencing `[Obsolete]`-marked symbols, grouped by deprecation message and severity. Sharper than generic `find_attribute_usages` for migration-planning workflows.

**Architecture:** Use `SymbolResolver.AttributeIndex` (already pre-built, keyed by simple attribute name) to enumerate every `[Obsolete]`-marked symbol with O(1) lookup. For each, walk all production syntax trees once collecting references via `SemanticModel.GetSymbolInfo`. Group by `(symbol, message, isError)` and sort.

**Tech Stack:** Roslyn (`Microsoft.CodeAnalysis`, `Microsoft.CodeAnalysis.CSharp.Syntax`), xUnit, BenchmarkDotNet, ModelContextProtocol.Server.

**Reference design:** `docs/plans/2026-05-01-find-obsolete-usage-design.md`

**Patterns to mirror:**
- AttributeIndex lookup: `src/RoslynCodeLens/Tools/FindAttributeUsagesLogic.cs:19`
- Per-target-set syntax walk: `src/RoslynCodeLens/Tools/FindCallersLogic.cs`
- MCP wrapper / auto-registration: any tool in `src/RoslynCodeLens/Tools/`; `Program.cs:35` uses `WithToolsFromAssembly()` — no edit needed.

---

## Task 1: Models

**Files:**
- Create: `src/RoslynCodeLens/Models/ObsoleteUsageSite.cs`
- Create: `src/RoslynCodeLens/Models/ObsoleteSymbolGroup.cs`
- Create: `src/RoslynCodeLens/Models/FindObsoleteUsageResult.cs`

**Step 1: `ObsoleteUsageSite.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record ObsoleteUsageSite(
    string CallerName,
    string FilePath,
    int Line,
    string Snippet,
    string Project,
    bool IsGenerated);
```

**Step 2: `ObsoleteSymbolGroup.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record ObsoleteSymbolGroup(
    string SymbolName,
    string DeprecationMessage,
    bool IsError,
    int UsageCount,
    IReadOnlyList<ObsoleteUsageSite> Usages);
```

**Step 3: `FindObsoleteUsageResult.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record FindObsoleteUsageResult(
    IReadOnlyList<ObsoleteSymbolGroup> Groups);
```

**Step 4: Build**

```bash
dotnet build src/RoslynCodeLens
```

Expected: 0 errors.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Models/ObsoleteUsageSite.cs \
  src/RoslynCodeLens/Models/ObsoleteSymbolGroup.cs \
  src/RoslynCodeLens/Models/FindObsoleteUsageResult.cs
git commit -m "feat: add models for find_obsolete_usage"
```

---

## Task 2: Test fixture

**Files:**
- Create: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/ObsoleteSamples.cs`

**Step 1: Create the fixture**

```csharp
using System;

namespace TestLib.ObsoleteSamples;

public class ObsoleteApi
{
    [Obsolete("Use NewWay instead")]
    public void ObsoleteWarning() { }

    [Obsolete("Hard fail", true)]
    public void ObsoleteError() { }

    [Obsolete]
    public void ObsoleteWithoutMessage() { }

    // Unused obsolete — must NOT appear in results (no usages, no migration needed).
    [Obsolete("Should not appear")]
    public void UnusedObsolete() { }
}

[Obsolete("Drop this type")]
public class ObsoleteType
{
    public void Bar() { }
}

public class ObsoleteConsumer
{
    private readonly ObsoleteApi _api = new();

    public void UseAll()
    {
        _api.ObsoleteWarning();      // call site #1 for ObsoleteWarning
        _api.ObsoleteWarning();      // call site #2 for ObsoleteWarning (counts as 2 usages)
        _api.ObsoleteError();        // call site for ObsoleteError
        _api.ObsoleteWithoutMessage(); // call site for ObsoleteWithoutMessage
    }

    public void UseObsoleteType()
    {
        var t = new ObsoleteType();   // ObjectCreation reference to obsolete type
        var name = nameof(ObsoleteType); // identifier reference (no method call)
    }
}
```

**Step 2: Build**

```bash
dotnet build tests/RoslynCodeLens.Tests
```

Expected: 0 errors. NOTE: the fixture itself contains `[Obsolete]` calls — those will produce **CS0612/CS0618** warnings during compilation. That's fine; they're warnings, not errors. The `GetDiagnostics_CleanSolution_ReturnsNoErrors` test asserts `Assert.Empty(errors-only)`, not all diagnostics — verify this in the test.

If `GetDiagnostics_CleanSolution_ReturnsNoErrors` checks for **any** diagnostic (warnings included), suppress these warnings inside `ObsoleteConsumer` with `#pragma warning disable CS0612, CS0618` blocks around the calls.

**Step 3: Sanity-check diagnostics**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetDiagnosticsToolTests" --no-build
```

Expected: green. If `GetDiagnostics_CleanSolution_ReturnsNoErrors` fails because of CS0612/CS0618, wrap the obsolete-using methods in `#pragma warning disable CS0612, CS0618` ... `#pragma warning restore CS0612, CS0618` and rebuild.

**Step 4: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/ObsoleteSamples.cs
git commit -m "test: add ObsoleteSamples fixture"
```

---

## Task 3: `FindObsoleteUsageLogic` + 12 tests (TDD)

**Files:**
- Create: `src/RoslynCodeLens/Tools/FindObsoleteUsageLogic.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/FindObsoleteUsageToolTests.cs`

**Step 1: Write failing tests**

`tests/RoslynCodeLens.Tests/Tools/FindObsoleteUsageToolTests.cs`:

```csharp
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindObsoleteUsageToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Result_FindsObsoleteWithMessage()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        var group = Assert.Single(result.Groups, g =>
            g.SymbolName.Contains("ObsoleteWarning", StringComparison.Ordinal));
        Assert.Equal("Use NewWay instead", group.DeprecationMessage);
        Assert.False(group.IsError);
        Assert.Equal(2, group.UsageCount);
    }

    [Fact]
    public void Result_FindsObsoleteError()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        var group = Assert.Single(result.Groups, g =>
            g.SymbolName.Contains("ObsoleteError", StringComparison.Ordinal));
        Assert.Equal("Hard fail", group.DeprecationMessage);
        Assert.True(group.IsError);
    }

    [Fact]
    public void Result_FindsObsoleteWithoutMessage()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        var group = Assert.Single(result.Groups, g =>
            g.SymbolName.Contains("ObsoleteWithoutMessage", StringComparison.Ordinal));
        Assert.Equal(string.Empty, group.DeprecationMessage);
        Assert.False(group.IsError);
    }

    [Fact]
    public void Result_FindsObsoleteType()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        var group = Assert.Single(result.Groups, g =>
            g.SymbolName.Contains("ObsoleteType", StringComparison.Ordinal));
        Assert.Equal("Drop this type", group.DeprecationMessage);
        Assert.True(group.UsageCount >= 1);
    }

    [Fact]
    public void Result_OmitsObsoleteWithZeroUsages()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        Assert.DoesNotContain(result.Groups, g =>
            g.SymbolName.Contains("UnusedObsolete", StringComparison.Ordinal));
    }

    [Fact]
    public void Sort_ErrorsBeforeWarnings()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        // Walk groups: once we see a non-error, every subsequent group must also be non-error.
        var sawWarning = false;
        foreach (var g in result.Groups)
        {
            if (!g.IsError) sawWarning = true;
            else if (sawWarning)
                Assert.Fail($"Error group '{g.SymbolName}' appears after a warning group; sort broken.");
        }
    }

    [Fact]
    public void Sort_WithinSameSeverity_HighestUsageFirst()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        for (int i = 1; i < result.Groups.Count; i++)
        {
            var prev = result.Groups[i - 1];
            var curr = result.Groups[i];
            if (prev.IsError == curr.IsError)
                Assert.True(prev.UsageCount >= curr.UsageCount,
                    $"Sort broken at {i}: '{prev.SymbolName}' ({prev.UsageCount}) before '{curr.SymbolName}' ({curr.UsageCount})");
        }
    }

    [Fact]
    public void ErrorOnlyFilter_ExcludesWarnings()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: true);

        Assert.All(result.Groups, g => Assert.True(g.IsError, $"Group {g.SymbolName} is warning-level but errorOnly=true"));
    }

    [Fact]
    public void ProjectFilter_OnlyReturnsRequestedProject()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: "TestLib", errorOnly: false);

        Assert.NotEmpty(result.Groups);
        Assert.All(result.Groups, g =>
            Assert.All(g.Usages, u => Assert.Equal("TestLib", u.Project)));
    }

    [Fact]
    public void Usages_SortedByFileLine()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        foreach (var group in result.Groups)
        {
            for (int i = 1; i < group.Usages.Count; i++)
            {
                var prev = group.Usages[i - 1];
                var curr = group.Usages[i];
                var fileCmp = string.CompareOrdinal(prev.FilePath, curr.FilePath);
                Assert.True(fileCmp < 0 || (fileCmp == 0 && prev.Line <= curr.Line),
                    $"Usage sort broken in {group.SymbolName} at {i}");
            }
        }
    }

    [Fact]
    public void TestProjects_AreSkipped()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        Assert.All(result.Groups, g =>
            Assert.All(g.Usages, u => Assert.NotEqual("RoslynCodeLens.Tests", u.Project)));
    }

    [Fact]
    public void Usages_HaveLocationInfo()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        foreach (var g in result.Groups)
        {
            Assert.All(g.Usages, u =>
            {
                Assert.NotEmpty(u.FilePath);
                Assert.True(u.Line > 0);
                Assert.NotEmpty(u.CallerName);
                Assert.NotEmpty(u.Snippet);
            });
        }
    }
}
```

**Step 2: Run to verify failure**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindObsoleteUsageToolTests"
```

Expect compile error: `FindObsoleteUsageLogic` not found.

**Step 3: Create `src/RoslynCodeLens/Tools/FindObsoleteUsageLogic.cs`**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class FindObsoleteUsageLogic
{
    public static FindObsoleteUsageResult Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string? project,
        bool errorOnly)
    {
        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);

        // Step 1: collect all [Obsolete]-marked symbols in production projects.
        // SymbolResolver.AttributeIndex is keyed by simple attribute name; entries are
        // duplicated under both "Obsolete" and "ObsoleteAttribute" — dedup by symbol identity.
        var obsoleteSymbols = new Dictionary<ISymbol, ObsoleteAttributeData>(SymbolEqualityComparer.Default);
        foreach (var key in new[] { "Obsolete", "ObsoleteAttribute" })
        {
            if (!resolver.AttributeIndex.TryGetValue(key, out var entries)) continue;

            foreach (var (symbol, attr) in entries)
            {
                if (attr.AttributeClass?.ToDisplayString() != "System.ObsoleteAttribute") continue;
                if (obsoleteSymbols.ContainsKey(symbol)) continue;

                obsoleteSymbols[symbol] = ParseObsoleteAttribute(attr);
            }
        }

        if (obsoleteSymbols.Count == 0)
            return new FindObsoleteUsageResult([]);

        // Step 2: walk all production syntax trees and collect usages per obsolete symbol.
        var targetSet = new HashSet<ISymbol>(obsoleteSymbols.Keys, SymbolEqualityComparer.Default);
        var usagesByTarget = new Dictionary<ISymbol, List<ObsoleteUsageSite>>(SymbolEqualityComparer.Default);
        foreach (var s in obsoleteSymbols.Keys)
            usagesByTarget[s] = new List<ObsoleteUsageSite>();

        var seen = new HashSet<(string, TextSpan)>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            if (testProjectIds.Contains(projectId)) continue;

            var projectName = resolver.GetProjectName(projectId);
            if (project is not null && !string.Equals(projectName, project, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                foreach (var node in root.DescendantNodes())
                {
                    if (node is not (InvocationExpressionSyntax
                                     or ObjectCreationExpressionSyntax
                                     or MemberAccessExpressionSyntax
                                     or IdentifierNameSyntax))
                        continue;

                    // Skip [Obsolete] attribute declarations themselves so the attribute target
                    // doesn't count as a usage of the symbol it marks.
                    if (node.FirstAncestorOrSelf<AttributeSyntax>() is not null)
                        continue;

                    var symbol = semanticModel.GetSymbolInfo(node).Symbol;
                    if (symbol is null) continue;

                    // Constructor calls land on IMethodSymbol (the ctor); we want the type's
                    // [Obsolete] flag too, so we accept either the symbol itself or its containing type.
                    var matched = MatchObsoleteSymbol(symbol, targetSet);
                    if (matched is null) continue;

                    var lineSpan = node.GetLocation().GetLineSpan();
                    var file = lineSpan.Path;
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var span = node.Span;

                    if (!seen.Add((file, span))) continue;

                    var callerName = GetCallerName(node);
                    var snippet = node.ToString();

                    usagesByTarget[matched].Add(new ObsoleteUsageSite(
                        CallerName: callerName,
                        FilePath: file,
                        Line: line,
                        Snippet: snippet,
                        Project: projectName,
                        IsGenerated: resolver.IsGenerated(file)));
                }
            }
        }

        // Step 3: build groups, drop zero-usage symbols.
        var groups = new List<ObsoleteSymbolGroup>();
        foreach (var (symbol, data) in obsoleteSymbols)
        {
            var usages = usagesByTarget[symbol];
            if (usages.Count == 0) continue;

            if (errorOnly && !data.IsError) continue;

            usages.Sort((a, b) =>
            {
                var fileCmp = string.CompareOrdinal(a.FilePath, b.FilePath);
                return fileCmp != 0 ? fileCmp : a.Line.CompareTo(b.Line);
            });

            groups.Add(new ObsoleteSymbolGroup(
                SymbolName: symbol.ToDisplayString(),
                DeprecationMessage: data.Message,
                IsError: data.IsError,
                UsageCount: usages.Count,
                Usages: usages));
        }

        // Step 4: sort groups: errors desc, usage count desc, name asc.
        groups.Sort((a, b) =>
        {
            var bySeverity = b.IsError.CompareTo(a.IsError);
            if (bySeverity != 0) return bySeverity;
            var byCount = b.UsageCount.CompareTo(a.UsageCount);
            if (byCount != 0) return byCount;
            return string.CompareOrdinal(a.SymbolName, b.SymbolName);
        });

        return new FindObsoleteUsageResult(groups);
    }

    private record ObsoleteAttributeData(string Message, bool IsError);

    private static ObsoleteAttributeData ParseObsoleteAttribute(AttributeData attr)
    {
        var message = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string s
            ? s
            : string.Empty;
        var isError = attr.ConstructorArguments.Length > 1 && attr.ConstructorArguments[1].Value is bool b && b;
        return new ObsoleteAttributeData(message, isError);
    }

    private static ISymbol? MatchObsoleteSymbol(ISymbol referenced, HashSet<ISymbol> targetSet)
    {
        if (targetSet.Contains(referenced)) return referenced;
        if (targetSet.Contains(referenced.OriginalDefinition)) return referenced.OriginalDefinition;

        // Constructor calls: also match the containing type (when the type itself has [Obsolete]).
        if (referenced is IMethodSymbol m && m.MethodKind == MethodKind.Constructor)
        {
            if (m.ContainingType is not null && targetSet.Contains(m.ContainingType))
                return m.ContainingType;
            if (m.ContainingType?.OriginalDefinition is { } ctorTypeOrig && targetSet.Contains(ctorTypeOrig))
                return ctorTypeOrig;
        }

        return null;
    }

    private static string GetCallerName(SyntaxNode node)
    {
        var method = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var type = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();

        if (type != null && method != null)
            return $"{type.Identifier.Text}.{method.Identifier.Text}";
        if (type != null)
            return type.Identifier.Text;
        return "<unknown>";
    }
}
```

**Step 4: Run tests**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindObsoleteUsageToolTests" --no-build
```

If `--no-build` fails, run `dotnet build` first. Expect 12/12 pass.

**Common debugging:**
- If tests fail because `Result_FindsObsoleteWithMessage` reports `UsageCount: 1` instead of 2: dedup is too aggressive. The `seen.Add((file, span))` keys by `node.Span`, which differs for two distinct invocation nodes — should work.
- If `Result_FindsObsoleteType` fails: the `MatchObsoleteSymbol` constructor-fallback handles `new ObsoleteType()` correctly. For `nameof(ObsoleteType)`, the `IdentifierNameSyntax` path resolves to the type itself.
- If `Result_OmitsObsoleteWithZeroUsages` fails: the zero-usage filter is in step 3 (`if (usages.Count == 0) continue`).

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/FindObsoleteUsageLogic.cs \
  tests/RoslynCodeLens.Tests/Tools/FindObsoleteUsageToolTests.cs
git commit -m "feat: add FindObsoleteUsageLogic grouped by deprecation message"
```

---

## Task 4: MCP wrapper

**Files:**
- Create: `src/RoslynCodeLens/Tools/FindObsoleteUsageTool.cs`

**Step 1: Create the wrapper**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindObsoleteUsageTool
{
    [McpServerTool(Name = "find_obsolete_usage")]
    [Description(
        "Find every call site referencing [Obsolete]-marked symbols in the solution, " +
        "grouped by deprecation message and severity. Sharper than find_attribute_usages " +
        "for migration-planning workflows: tells you 'we have 5 distinct deprecations " +
        "pending; this one has 80 sites and is an error; that one has 3 sites and is a " +
        "warning.' " +
        "Includes both source-marked and metadata-marked obsoletes (third-party NuGet " +
        "deprecations are surfaced too). " +
        "Symbols with zero usages are omitted (no migration needed). " +
        "Sort: errors first, then by usage count descending, then by symbol name. " +
        "Test projects skipped. Project filter is case-insensitive.")]
    public static FindObsoleteUsageResult Execute(
        MultiSolutionManager manager,
        [Description("Optional: restrict to a single project by name (case-insensitive).")]
        string? project = null,
        [Description("If true, only [Obsolete(..., true)] error-level deprecations are returned. Default false.")]
        bool errorOnly = false)
    {
        manager.EnsureLoaded();
        return FindObsoleteUsageLogic.Execute(
            manager.GetLoadedSolution(),
            manager.GetResolver(),
            project,
            errorOnly);
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
dotnet test tests/RoslynCodeLens.Tests --filter "FindObsoleteUsageToolTests"
```

Expect 12/12 pass.

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Tools/FindObsoleteUsageTool.cs
git commit -m "feat: register find_obsolete_usage MCP tool"
```

---

## Task 5: Add benchmark

**Files:**
- Modify: `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

**Step 1**: Find the `find_attribute_usages: Obsolete` benchmark. Add the new one immediately after.

```csharp
[Benchmark(Description = "find_obsolete_usage: whole solution")]
public object FindObsoleteUsage()
{
    return FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);
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
git commit -m "bench: add find_obsolete_usage benchmark"
```

---

## Task 6: Update SKILL.md, README, CLAUDE.md

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
- Modify: `README.md`
- Modify: `CLAUDE.md`

**Step 1: SKILL.md — Red Flags routing table**

Add near `find_attribute_usages`:

```
| "What deprecations do we still use?" / "Plan an Obsolete cleanup" / "Find every [Obsolete] call site" | `find_obsolete_usage` |
```

**Step 2: SKILL.md — Quick Reference**

Add near `find_attribute_usages`:

```
| `find_obsolete_usage` | "What deprecations do we still use?" |
```

**Step 3: SKILL.md — Code Quality Analysis section**

Add a new bullet:

```
- `find_obsolete_usage` — Every [Obsolete] call site grouped by deprecation message and severity. Sharper than find_attribute_usages for migration planning. Source AND metadata obsoletes (NuGet) included.
```

**Step 4: README.md Features list**

Add near `find_attribute_usages`:

```
- **find_obsolete_usage** — Every [Obsolete] call site grouped by deprecation message and severity, errors first; for planning migrations
```

**Step 5: CLAUDE.md tool count**

Bump from 32 to 33.

**Step 6: Sanity check**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindObsoleteUsageToolTests"
```

Expect 12/12.

**Step 7: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md \
  README.md \
  CLAUDE.md
git commit -m "docs: announce find_obsolete_usage in SKILL.md, README, CLAUDE.md"
```

---

## Done

After Task 6 the branch should have ~8 commits (design + plan + 6 implementation tasks). 12/12 tests green, benchmark project compiling, tool auto-registered.
