# `find_god_objects` Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a new MCP tool `find_god_objects` that finds types crossing both a size axis (lines/members/fields) AND a coupling axis (incoming/outgoing namespace counts). Sharper signal than `find_large_classes` size alone.

**Architecture:** Walk types via `resolver.AllTypes`, filter to size-suspects first (cheap), then compute incoming-namespace coupling via a single solution-wide syntax walk batching all candidates and outgoing-namespace coupling via per-candidate body scan. Filter to types where both size AND coupling axes are exceeded. Sort by total axes exceeded.

**Tech Stack:** Roslyn (`Microsoft.CodeAnalysis`, `Microsoft.CodeAnalysis.CSharp.Syntax`), xUnit, BenchmarkDotNet, ModelContextProtocol.Server.

**Reference design:** `docs/plans/2026-04-30-find-god-objects-design.md`

**Patterns to mirror:**
- Walk pattern: `src/RoslynCodeLens/Tools/FindLargeClassesLogic.cs`
- Test-project filter: `RoslynCodeLens.TestDiscovery.TestProjectDetector.GetTestProjectIds(loaded.Solution)`
- Generated-code filter: `RoslynCodeLens.Analysis.GeneratedCodeDetector.IsGenerated(syntaxTree)`
- MCP wrapper / auto-registration: any tool in `src/RoslynCodeLens/Tools/`; `Program.cs:35` uses `WithToolsFromAssembly()` — no edit.

---

## Task 1: Models

**Files:**
- Create: `src/RoslynCodeLens/Models/GodObjectInfo.cs`
- Create: `src/RoslynCodeLens/Models/GodObjectsResult.cs`

**Step 1: `GodObjectInfo.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record GodObjectInfo(
    string TypeName,
    int LineCount,
    int MemberCount,
    int FieldCount,
    int IncomingNamespaces,
    int OutgoingNamespaces,
    IReadOnlyList<string> SampleIncoming,
    IReadOnlyList<string> SampleOutgoing,
    string FilePath,
    int Line,
    string Project,
    int SizeAxesExceeded,
    int CouplingAxesExceeded);
```

**Step 2: `GodObjectsResult.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record GodObjectsResult(
    IReadOnlyList<GodObjectInfo> Types);
```

**Step 3: Build**

```bash
dotnet build src/RoslynCodeLens
```

Expected: 0 errors.

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Models/GodObjectInfo.cs \
  src/RoslynCodeLens/Models/GodObjectsResult.cs
git commit -m "feat: add models for find_god_objects"
```

---

## Task 2: Test fixture

Self-contained fixture with one bad-god, one large-but-isolated, and one small-but-coupled.

**Files:**
- Create: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/GodObjectSamples.cs`

**Step 1: Create the fixture**

```csharp
namespace TestLib.GodObjectSamples.Bad;

// BadGodObject — large class with many members, called from multiple namespaces.
public class BadGodObject
{
    public int Field1;
    public int Field2;
    public int Field3;
    public int Field4;
    public int Field5;
    public int Field6;
    public int Field7;
    public int Field8;
    public int Field9;
    public int Field10;
    public int Field11;
    public int Field12;

    public void Method1() { }
    public void Method2() { }
    public void Method3() { }
    public void Method4() { }
    public void Method5() { }
    public void Method6() { }
    public void Method7() { }
    public void Method8() { }
    public void Method9() { }
    public void Method10() { }
    public void Method11() { }
    public void Method12() { }
    public void Method13() { }
    public void Method14() { }
    public void Method15() { }
    public void Method16() { }
}

// Padding lines to push past the 300-line threshold:
// Line 50
// Line 51
// ... (keep adding so the type's GetLineSpan crosses 300)
//
// (Use a single big multi-line comment after the closing brace of the namespace
//  to ensure the type's line span itself reaches the threshold; or pack 300+
//  blank lines below.)
```

**Note on padding**: The line count comes from `GetLineSpan(span)` of the type's declaration. The type itself needs to span 300+ lines. Easiest way: write 300+ blank lines or method bodies inside the class.

For a more compact fixture, drop `minLines` to e.g. 60 in tests via the `minLines` parameter — that's the design decision: thresholds are configurable so tests can use lower bars.

**Updated fixture (uses lower thresholds in tests):**

```csharp
using TestLib.GodObjectSamples.CallerA;
using TestLib.GodObjectSamples.CallerB;
using TestLib.GodObjectSamples.CallerC;
using TestLib.GodObjectSamples.CallerD;
using TestLib.GodObjectSamples.CallerE;

namespace TestLib.GodObjectSamples.Bad;

// 16 members, 12 fields → exceeds member + field axes.
// Referenced from 5 distinct namespaces (CallerA/B/C/D/E) → exceeds incoming-namespace axis.
public class BadGodObject
{
    public int Field1, Field2, Field3, Field4, Field5, Field6;
    public int Field7, Field8, Field9, Field10, Field11, Field12;
    public void Method1() { }
    public void Method2() { }
    public void Method3() { }
    public void Method4() { }
    public void Method5() { }
    public void Method6() { }
    public void Method7() { }
    public void Method8() { }
    public void Method9() { }
    public void Method10() { }
    public void Method11() { }
    public void Method12() { }
    public void Method13() { }
    public void Method14() { }
    public void Method15() { }
    public void Method16() { }
}

namespace TestLib.GodObjectSamples.Isolated;

// 16 members but only used by its own namespace.
public class LargeButIsolated
{
    public void Method1() { }
    public void Method2() { }
    public void Method3() { }
    public void Method4() { }
    public void Method5() { }
    public void Method6() { }
    public void Method7() { }
    public void Method8() { }
    public void Method9() { }
    public void Method10() { }
    public void Method11() { }
    public void Method12() { }
    public void Method13() { }
    public void Method14() { }
    public void Method15() { }
    public void Method16() { }
}

public class IsolatedConsumer
{
    public void Use()
    {
        var x = new LargeButIsolated();
        x.Method1();
    }
}

namespace TestLib.GodObjectSamples.Small;

// 1 member, but referenced from 5 namespaces (via Caller A-E).
public class SmallButHighlyCoupled
{
    public void OneMethod() { }
}

namespace TestLib.GodObjectSamples.CallerA;
public class A
{
    public void Use(BadGodObject b, SmallButHighlyCoupled s) { b.Method1(); s.OneMethod(); }
}

namespace TestLib.GodObjectSamples.CallerB;
public class B
{
    public void Use(BadGodObject b, SmallButHighlyCoupled s) { b.Method2(); s.OneMethod(); }
}

namespace TestLib.GodObjectSamples.CallerC;
public class C
{
    public void Use(BadGodObject b, SmallButHighlyCoupled s) { b.Method3(); s.OneMethod(); }
}

namespace TestLib.GodObjectSamples.CallerD;
public class D
{
    public void Use(BadGodObject b, SmallButHighlyCoupled s) { b.Method4(); s.OneMethod(); }
}

namespace TestLib.GodObjectSamples.CallerE;
public class E
{
    public void Use(BadGodObject b, SmallButHighlyCoupled s) { b.Method5(); s.OneMethod(); }
}
```

This fixture lets tests use `minMembers: 15, minFields: 10, minIncomingNamespaces: 5` and produce reliable hits without 300+ blank lines.

**Step 2: Build**

```bash
dotnet build tests/RoslynCodeLens.Tests
```

Expected: 0 errors.

**Step 3: Run existing tests** (sanity)

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindLargeClassesToolTests" --no-build
```

Expected: green.

**Step 4: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/GodObjectSamples.cs
git commit -m "test: add GodObjectSamples fixture (bad/isolated/coupled-but-small)"
```

---

## Task 3: `FindGodObjectsLogic` + comprehensive tests (TDD)

The walker. Two-pass: filter to size-suspects, then compute coupling for those.

**Files:**
- Create: `src/RoslynCodeLens/Tools/FindGodObjectsLogic.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/FindGodObjectsToolTests.cs`

**Step 1: Write the failing tests**

`tests/RoslynCodeLens.Tests/Tools/FindGodObjectsToolTests.cs`:

```csharp
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindGodObjectsToolTests : IAsyncLifetime
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
    public void Result_FindsKnownGodObject()
    {
        // Use lowered thresholds matching the fixture's shape.
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 1, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 1);

        Assert.Contains(result.Types, t =>
            t.TypeName.Contains("BadGodObject", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_DoesNotFlag_LargeButIsolated()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 1, minMembers: 15, minFields: 0,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 1);

        Assert.DoesNotContain(result.Types, t =>
            t.TypeName.Contains("LargeButIsolated", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_DoesNotFlag_SmallButHighlyCoupled()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 1, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 1);

        Assert.DoesNotContain(result.Types, t =>
            t.TypeName.Contains("SmallButHighlyCoupled", StringComparison.Ordinal));
    }

    [Fact]
    public void IncomingNamespaces_AreCounted()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 1, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 1);

        var bad = Assert.Single(result.Types, t =>
            t.TypeName.Contains("BadGodObject", StringComparison.Ordinal));
        Assert.True(bad.IncomingNamespaces >= 5);
    }

    [Fact]
    public void IncomingNamespaces_ExcludesOwnNamespace()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 1, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 1);

        var bad = Assert.Single(result.Types, t =>
            t.TypeName.Contains("BadGodObject", StringComparison.Ordinal));
        Assert.DoesNotContain(bad.SampleIncoming, ns =>
            ns == "TestLib.GodObjectSamples.Bad");
    }

    [Fact]
    public void OutgoingNamespaces_ExcludesBclTypes()
    {
        // BadGodObject's body uses int (System) — must not count.
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 1, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 0);

        var bad = Assert.Single(result.Types, t =>
            t.TypeName.Contains("BadGodObject", StringComparison.Ordinal));
        Assert.DoesNotContain(bad.SampleOutgoing, ns =>
            ns.StartsWith("System", StringComparison.Ordinal) ||
            ns.StartsWith("Microsoft", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_SortedByAxesExceededDesc()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: null,
            minLines: 1, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 0);

        for (int i = 1; i < result.Types.Count; i++)
        {
            var prev = result.Types[i - 1];
            var curr = result.Types[i];
            var prevTotal = prev.SizeAxesExceeded + prev.CouplingAxesExceeded;
            var currTotal = curr.SizeAxesExceeded + curr.CouplingAxesExceeded;
            Assert.True(prevTotal >= currTotal,
                $"Sort violation at {i}: prev={prevTotal}, curr={currTotal}");
        }
    }

    [Fact]
    public void ProjectFilter_OnlyReturnsRequestedProject()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 1, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 0);

        Assert.All(result.Types, t => Assert.Equal("TestLib", t.Project));
    }

    [Fact]
    public void Thresholds_AreConfigurable_HighThreshold_FiltersAll()
    {
        // Impossibly high threshold — nothing should be flagged.
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 100000, minMembers: 100000, minFields: 100000,
            minIncomingNamespaces: 100000, minOutgoingNamespaces: 100000);

        Assert.Empty(result.Types);
    }

    [Fact]
    public void SampleIncoming_LimitedToFive()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 1, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 0);

        Assert.All(result.Types, t => Assert.True(t.SampleIncoming.Count <= 5));
        Assert.All(result.Types, t => Assert.True(t.SampleOutgoing.Count <= 5));
    }

    [Fact]
    public void Interfaces_AreNotFlagged()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: null,
            minLines: 1, minMembers: 1, minFields: 0,
            minIncomingNamespaces: 1, minOutgoingNamespaces: 0);

        Assert.DoesNotContain(result.Types, t =>
            t.TypeName.Contains("IGreeter", StringComparison.Ordinal));
    }

    [Fact]
    public void TestProjects_AreSkipped()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: null,
            minLines: 1, minMembers: 1, minFields: 0,
            minIncomingNamespaces: 1, minOutgoingNamespaces: 0);

        Assert.DoesNotContain(result.Types, t => t.Project == "RoslynCodeLens.Tests");
    }
}
```

**Step 2: Run to verify failure**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindGodObjectsToolTests"
```

Expect compile error.

**Step 3: Create `src/RoslynCodeLens/Tools/FindGodObjectsLogic.cs`**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class FindGodObjectsLogic
{
    private const string SystemNamespacePrefix = "System";
    private const string MicrosoftNamespacePrefix = "Microsoft";

    public static GodObjectsResult Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string? project,
        int minLines,
        int minMembers,
        int minFields,
        int minIncomingNamespaces,
        int minOutgoingNamespaces)
    {
        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);

        // Pass 1 — collect size-suspects (cheap).
        var suspects = new List<SizeSuspect>();
        foreach (var type in resolver.AllTypes)
        {
            if (!IsCandidate(type, testProjectIds, loaded, resolver, project))
                continue;

            var lineCount = GetLineCount(type);
            var memberCount = type.GetMembers().Count(m => !m.IsImplicitlyDeclared);
            var fieldCount = type.GetMembers().OfType<IFieldSymbol>().Count(f => !f.IsImplicitlyDeclared);

            var sizeAxes =
                (lineCount >= minLines ? 1 : 0)
                + (memberCount >= minMembers ? 1 : 0)
                + (fieldCount >= minFields ? 1 : 0);

            if (sizeAxes == 0) continue;

            suspects.Add(new SizeSuspect(type, lineCount, memberCount, fieldCount, sizeAxes));
        }

        if (suspects.Count == 0)
            return new GodObjectsResult([]);

        // Pass 2 — compute incoming-namespace coupling for each suspect with a single solution-wide walk.
        var incomingByType = ComputeIncomingNamespaces(loaded, suspects);

        // Pass 3 — compute outgoing-namespace coupling per suspect.
        var results = new List<GodObjectInfo>();
        foreach (var s in suspects)
        {
            var ownNamespace = s.Type.ContainingNamespace?.ToDisplayString() ?? string.Empty;

            var incoming = incomingByType.TryGetValue(s.Type, out var inSet) ? inSet : new HashSet<string>(StringComparer.Ordinal);
            var outgoing = ComputeOutgoingNamespaces(loaded, s.Type, ownNamespace);

            var couplingAxes =
                (incoming.Count >= minIncomingNamespaces ? 1 : 0)
                + (outgoing.Count >= minOutgoingNamespaces ? 1 : 0);

            if (couplingAxes == 0) continue;

            var (file, line) = resolver.GetFileAndLine(s.Type);
            results.Add(new GodObjectInfo(
                TypeName: s.Type.ToDisplayString(),
                LineCount: s.LineCount,
                MemberCount: s.MemberCount,
                FieldCount: s.FieldCount,
                IncomingNamespaces: incoming.Count,
                OutgoingNamespaces: outgoing.Count,
                SampleIncoming: incoming.OrderBy(x => x, StringComparer.Ordinal).Take(5).ToList(),
                SampleOutgoing: outgoing.OrderBy(x => x, StringComparer.Ordinal).Take(5).ToList(),
                FilePath: file,
                Line: line,
                Project: resolver.GetProjectName(s.Type),
                SizeAxesExceeded: s.SizeAxes,
                CouplingAxesExceeded: couplingAxes));
        }

        results.Sort((a, b) =>
        {
            var totalA = a.SizeAxesExceeded + a.CouplingAxesExceeded;
            var totalB = b.SizeAxesExceeded + b.CouplingAxesExceeded;
            var byTotal = totalB.CompareTo(totalA);
            if (byTotal != 0) return byTotal;
            return b.LineCount.CompareTo(a.LineCount);
        });

        return new GodObjectsResult(results);
    }

    private record SizeSuspect(INamedTypeSymbol Type, int LineCount, int MemberCount, int FieldCount, int SizeAxes);

    private static bool IsCandidate(
        INamedTypeSymbol type,
        HashSet<ProjectId> testProjectIds,
        LoadedSolution loaded,
        SymbolResolver resolver,
        string? project)
    {
        if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct) return false;
        if (type.ContainingType is not null) return false; // skip nested
        if (type.IsImplicitlyDeclared) return false;
        if (!type.Locations.Any(l => l.IsInSource)) return false;

        var location = type.Locations.First(l => l.IsInSource);
        if (location.SourceTree is not null && GeneratedCodeDetector.IsGenerated(location.SourceTree)) return false;

        var projectName = resolver.GetProjectName(type);
        var projectId = loaded.Solution.Projects
            .FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.Ordinal))?.Id;
        if (projectId is not null && testProjectIds.Contains(projectId)) return false;

        if (project is not null && !string.Equals(projectName, project, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static Dictionary<INamedTypeSymbol, HashSet<string>> ComputeIncomingNamespaces(
        LoadedSolution loaded, List<SizeSuspect> suspects)
    {
        var byType = new Dictionary<INamedTypeSymbol, HashSet<string>>(SymbolEqualityComparer.Default);
        foreach (var s in suspects)
            byType[s.Type] = new HashSet<string>(StringComparer.Ordinal);

        var suspectSet = new HashSet<INamedTypeSymbol>(suspects.Select(s => s.Type), SymbolEqualityComparer.Default);

        foreach (var (_, compilation) in loaded.Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (GeneratedCodeDetector.IsGenerated(tree)) continue;

                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                foreach (var node in root.DescendantNodes())
                {
                    var symbol = semanticModel.GetSymbolInfo(node).Symbol;
                    if (symbol is null) continue;

                    var containingType = (symbol as INamedTypeSymbol)?.OriginalDefinition
                        ?? symbol.ContainingType?.OriginalDefinition;
                    if (containingType is null) continue;

                    if (!suspectSet.Contains(containingType)) continue;

                    var callerNamespace = GetEnclosingNamespace(node, semanticModel);
                    if (callerNamespace is null) continue;

                    var ownNamespace = containingType.ContainingNamespace?.ToDisplayString();
                    if (string.Equals(callerNamespace, ownNamespace, StringComparison.Ordinal)) continue;

                    byType[containingType].Add(callerNamespace);
                }
            }
        }

        return byType;
    }

    private static HashSet<string> ComputeOutgoingNamespaces(
        LoadedSolution loaded, INamedTypeSymbol type, string ownNamespace)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declRef in type.DeclaringSyntaxReferences)
        {
            var tree = declRef.SyntaxTree;
            var compilation = loaded.Compilations.FirstOrDefault(c => c.Item2.SyntaxTrees.Contains(tree)).Item2;
            if (compilation is null) continue;

            var semanticModel = compilation.GetSemanticModel(tree);
            var bodyNode = declRef.GetSyntax();

            foreach (var node in bodyNode.DescendantNodes())
            {
                var symbol = semanticModel.GetSymbolInfo(node).Symbol;
                if (symbol is null) continue;

                var ns = symbol.ContainingType?.ContainingNamespace?.ToDisplayString()
                      ?? (symbol as INamedTypeSymbol)?.ContainingNamespace?.ToDisplayString();
                if (string.IsNullOrEmpty(ns)) continue;

                if (string.Equals(ns, ownNamespace, StringComparison.Ordinal)) continue;
                if (ns.StartsWith(SystemNamespacePrefix, StringComparison.Ordinal)) continue;
                if (ns.StartsWith(MicrosoftNamespacePrefix, StringComparison.Ordinal)) continue;

                result.Add(ns);
            }
        }

        return result;
    }

    private static string? GetEnclosingNamespace(SyntaxNode node, SemanticModel semanticModel)
    {
        var declaration = node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        if (declaration is null) return null;

        var typeSymbol = semanticModel.GetDeclaredSymbol(declaration);
        return typeSymbol?.ContainingNamespace?.ToDisplayString();
    }

    private static int GetLineCount(INamedTypeSymbol type)
    {
        var syntaxRef = type.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef is null) return 0;
        var span = syntaxRef.Span;
        var tree = syntaxRef.SyntaxTree;
        var lineSpan = tree.GetLineSpan(span);
        return lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1;
    }
}
```

**Step 4: Run tests**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindGodObjectsToolTests" -v normal
```

Expect 12/12 pass.

**Common debugging:**
- If `Result_FindsKnownGodObject` fails (empty result): check that `TestLib` project is in the loaded solution AND that `BadGodObject` has 16 methods + 12 fields. Lower thresholds in the test if the fixture changed.
- If `IncomingNamespaces_AreCounted` reports `< 5`: the incoming-namespace walker must visit every syntax tree in every compilation; ensure neither the compilation loop nor the syntax-tree loop is short-circuiting.
- If `OutgoingNamespaces_ExcludesBclTypes` fails: ensure the `StartsWith("System") || StartsWith("Microsoft")` filter runs.
- If the BadGodObject's incoming namespaces include `TestLib.GodObjectSamples.Bad`: the own-namespace exclusion isn't matching — verify `containingType.ContainingNamespace?.ToDisplayString()` returns the same string format as `GetEnclosingNamespace`.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/FindGodObjectsLogic.cs \
  tests/RoslynCodeLens.Tests/Tools/FindGodObjectsToolTests.cs
git commit -m "feat: add FindGodObjectsLogic with size+coupling axes"
```

---

## Task 4: MCP wrapper

**Files:**
- Create: `src/RoslynCodeLens/Tools/FindGodObjectsTool.cs`

**Step 1: Create the wrapper**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindGodObjectsTool
{
    [McpServerTool(Name = "find_god_objects")]
    [Description(
        "Find types that combine high size with high coupling — 'god classes' that violate " +
        "single-responsibility and become refactoring nightmares. Sharper signal than " +
        "find_large_classes alone: a 1000-line internal helper used only by its own " +
        "namespace is not flagged, but a 200-line class called from 15 different " +
        "namespaces is. " +
        "Two axes: size (lines/members/fields) and coupling (incoming/outgoing namespace " +
        "counts). A type qualifies when it crosses BOTH axes. " +
        "Defaults: lines >= 300, members >= 15, fields >= 10, incoming-namespaces >= 5, " +
        "outgoing-namespaces >= 5. Each axis is independently configurable. " +
        "BCL namespaces (System.*, Microsoft.*) excluded from outgoing count. Test " +
        "projects, generated code, interfaces, and nested types are all skipped. " +
        "Sort: total axes exceeded DESC, then line count DESC.")]
    public static GodObjectsResult Execute(
        MultiSolutionManager manager,
        [Description("Optional: restrict to a single project by name (case-insensitive).")]
        string? project = null,
        [Description("Min lines for size axis. Default 300.")]
        int minLines = 300,
        [Description("Min member count for size axis. Default 15.")]
        int minMembers = 15,
        [Description("Min field count for size axis. Default 10.")]
        int minFields = 10,
        [Description("Min incoming-namespace count for coupling axis. Default 5.")]
        int minIncomingNamespaces = 5,
        [Description("Min outgoing-namespace count for coupling axis. Default 5.")]
        int minOutgoingNamespaces = 5)
    {
        manager.EnsureLoaded();
        return FindGodObjectsLogic.Execute(
            manager.GetLoadedSolution(),
            manager.GetResolver(),
            project,
            minLines,
            minMembers,
            minFields,
            minIncomingNamespaces,
            minOutgoingNamespaces);
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
dotnet test tests/RoslynCodeLens.Tests --filter "FindGodObjectsToolTests"
```

Expect 12/12 pass.

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Tools/FindGodObjectsTool.cs
git commit -m "feat: register find_god_objects MCP tool"
```

---

## Task 5: Add benchmark

**Files:**
- Modify: `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

**Step 1**: Find the existing `find_large_classes: all` benchmark and add immediately after.

```csharp
[Benchmark(Description = "find_god_objects: whole solution")]
public object FindGodObjects()
{
    return FindGodObjectsLogic.Execute(
        _loaded, _resolver,
        project: null,
        minLines: 300, minMembers: 15, minFields: 10,
        minIncomingNamespaces: 5, minOutgoingNamespaces: 5);
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
git commit -m "bench: add find_god_objects benchmark"
```

---

## Task 6: Update SKILL.md, README, CLAUDE.md

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
- Modify: `README.md`
- Modify: `CLAUDE.md`

**Step 1: SKILL.md — Red Flags routing table**

Add near `find_large_classes`:

```
| "Which classes are doing too much?" / "Where are my god classes?" / "Worst design smells in this codebase?" | `find_god_objects` |
```

**Step 2: SKILL.md — Quick Reference**

Add near `find_large_classes`:

```
| `find_god_objects` | "Which classes are doing too much?" |
```

**Step 3: SKILL.md — Code Quality Analysis section**

Add after `find_large_classes` bullet:

```
- `find_god_objects` — Types crossing both a size axis (lines/members/fields) AND a coupling axis (incoming/outgoing namespaces). Sharper than raw size: a large but isolated class won't flag; a 200-line class with 15 callers across many namespaces will.
```

**Step 4: README.md Features list**

Add near `find_large_classes`:

```
- **find_god_objects** — Types combining high size with high cross-namespace coupling; sharper signal than raw size for SRP violations
```

**Step 5: CLAUDE.md tool count**

Bump from 31 to 32.

**Step 6: Sanity check**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindGodObjectsToolTests"
```

Expect 12/12.

**Step 7: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md \
  README.md \
  CLAUDE.md
git commit -m "docs: announce find_god_objects in SKILL.md, README, CLAUDE.md"
```

---

## Done

After Task 6 the branch should have ~8 commits (design + plan + 6 implementation tasks), all `FindGodObjectsToolTests` green, the benchmark project compiling, and the tool auto-registered. From there: `/superpowers:requesting-code-review` → PR.
