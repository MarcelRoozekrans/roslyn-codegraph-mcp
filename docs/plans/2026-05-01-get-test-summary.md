# `get_test_summary` Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a new MCP tool `get_test_summary` that returns a per-project inventory of test methods, including framework/attribute kind, data-row count, source location, and the production symbols each test references.

**Architecture:** Walk all test projects (via `TestProjectDetector.GetTestProjectIds`). For each project's compilation, recursively enumerate types and methods, filter via `TestMethodClassifier.IsTestMethod`. Per test method, capture classification (framework + attribute short name), count data-row attributes, walk method body for production-symbol references (excluding framework + BCL namespaces). Group by project, build counts, sort.

**Tech Stack:** Roslyn (`Microsoft.CodeAnalysis`, `Microsoft.CodeAnalysis.CSharp.Syntax`), xUnit, BenchmarkDotNet, ModelContextProtocol.Server.

**Reference design:** `docs/plans/2026-05-01-get-test-summary-design.md`

**Patterns to mirror:**
- Test-project filter: `RoslynCodeLens.TestDiscovery.TestProjectDetector.GetTestProjectIds(loaded.Solution)` (returns ImmutableHashSet<ProjectId>)
- Test-method classification: `RoslynCodeLens.TestDiscovery.TestMethodClassifier.Classify(method)` returns `TestMethodClassification?` with `Framework` (`TestFramework` enum: XUnit/NUnit/MSTest) and `AttributeShortName`
- Composite tool pattern: `src/RoslynCodeLens/Tools/GetProjectHealthLogic.cs`
- MCP wrapper auto-registration: `Program.cs:35` uses `WithToolsFromAssembly()` — no edit needed.

---

## Task 1: Models

**Files:**
- Create: `src/RoslynCodeLens/Models/TestMethodSummary.cs`
- Create: `src/RoslynCodeLens/Models/ProjectTestSummary.cs`
- Create: `src/RoslynCodeLens/Models/GetTestSummaryResult.cs`

**Step 1: `TestMethodSummary.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record TestMethodSummary(
    string MethodName,
    string Framework,
    string AttributeShortName,
    int InlineDataRowCount,
    IReadOnlyList<string> ReferencedSymbols,
    string FilePath,
    int Line);
```

**Step 2: `ProjectTestSummary.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record ProjectTestSummary(
    string Project,
    int TotalTests,
    IReadOnlyDictionary<string, int> ByFramework,
    IReadOnlyDictionary<string, int> ByAttribute,
    IReadOnlyList<TestMethodSummary> Tests);
```

**Step 3: `GetTestSummaryResult.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record GetTestSummaryResult(
    IReadOnlyList<ProjectTestSummary> Projects);
```

**Step 4: Build**

```bash
dotnet build src/RoslynCodeLens
```

Expected: 0 errors.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Models/TestMethodSummary.cs \
  src/RoslynCodeLens/Models/ProjectTestSummary.cs \
  src/RoslynCodeLens/Models/GetTestSummaryResult.cs
git commit -m "feat: add models for get_test_summary"
```

---

## Task 2: `GetTestSummaryLogic` + tests (TDD)

**Files:**
- Create: `src/RoslynCodeLens/Tools/GetTestSummaryLogic.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/GetTestSummaryToolTests.cs`

**Step 1: Write the failing tests**

`tests/RoslynCodeLens.Tests/Tools/GetTestSummaryToolTests.cs`:

```csharp
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetTestSummaryToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GetTestSummaryToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void Result_FindsXUnitTests()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);

        var xUnitProject = Assert.Single(result.Projects, p => p.Project == "XUnitFixture");
        Assert.True(xUnitProject.TotalTests > 0);
        Assert.Contains("XUnit", xUnitProject.ByFramework.Keys);
    }

    [Fact]
    public void Result_FindsNUnitTests()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);

        var nUnitProject = Assert.Single(result.Projects, p => p.Project == "NUnitFixture");
        Assert.True(nUnitProject.TotalTests > 0);
        Assert.Contains("NUnit", nUnitProject.ByFramework.Keys);
    }

    [Fact]
    public void Result_FindsMSTestTests()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);

        var msTestProject = Assert.Single(result.Projects, p => p.Project == "MSTestFixture");
        Assert.True(msTestProject.TotalTests > 0);
        Assert.Contains("MSTest", msTestProject.ByFramework.Keys);
    }

    [Fact]
    public void InlineDataRowCount_PopulatedForXUnitTheory()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: "XUnitFixture");

        var project = Assert.Single(result.Projects);
        // SampleTests has a [Theory] with [InlineData] rows — count must be > 0.
        var theory = project.Tests.FirstOrDefault(t =>
            string.Equals(t.AttributeShortName, "Theory", StringComparison.Ordinal));
        Assert.NotNull(theory);
        Assert.True(theory!.InlineDataRowCount > 0,
            $"Expected InlineDataRowCount > 0 for theory {theory.MethodName}, got {theory.InlineDataRowCount}");
    }

    [Fact]
    public void InlineDataRowCount_ZeroForFact()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: "XUnitFixture");

        var project = Assert.Single(result.Projects);
        var fact = project.Tests.FirstOrDefault(t =>
            string.Equals(t.AttributeShortName, "Fact", StringComparison.Ordinal));
        Assert.NotNull(fact);
        Assert.Equal(0, fact!.InlineDataRowCount);
    }

    [Fact]
    public void ReferencedSymbols_IncludeProductionCalls()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: "XUnitFixture");

        var project = Assert.Single(result.Projects);
        // SampleTests calls Greeter.Greet — every test should list a TestLib production symbol.
        Assert.Contains(project.Tests, t =>
            t.ReferencedSymbols.Any(s => s.Contains("TestLib", StringComparison.Ordinal)));
    }

    [Fact]
    public void ReferencedSymbols_ExcludesFrameworkAndBcl()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: "XUnitFixture");

        var project = Assert.Single(result.Projects);
        foreach (var test in project.Tests)
        {
            foreach (var symbol in test.ReferencedSymbols)
            {
                Assert.False(symbol.StartsWith("Xunit", StringComparison.Ordinal),
                    $"Test {test.MethodName} referenced framework symbol {symbol}");
                Assert.False(symbol.StartsWith("System.", StringComparison.Ordinal) || symbol == "System",
                    $"Test {test.MethodName} referenced BCL symbol {symbol}");
                Assert.False(symbol.StartsWith("Microsoft.", StringComparison.Ordinal) || symbol == "Microsoft",
                    $"Test {test.MethodName} referenced Microsoft.* symbol {symbol}");
                Assert.False(symbol.StartsWith("NUnit.Framework", StringComparison.Ordinal),
                    $"Test {test.MethodName} referenced NUnit framework symbol {symbol}");
            }
        }
    }

    [Fact]
    public void ProjectFilter_OnlyReturnsRequestedProject()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: "XUnitFixture");

        var project = Assert.Single(result.Projects);
        Assert.Equal("XUnitFixture", project.Project);
    }

    [Fact]
    public void Result_OmitsProductionProjects()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);

        Assert.DoesNotContain(result.Projects, p => p.Project == "TestLib");
        Assert.DoesNotContain(result.Projects, p => p.Project == "TestLib2");
    }

    [Fact]
    public void ByFramework_CountsAreCorrect()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);

        foreach (var project in result.Projects)
        {
            foreach (var (frameworkName, count) in project.ByFramework)
            {
                var actual = project.Tests.Count(t =>
                    string.Equals(t.Framework, frameworkName, StringComparison.Ordinal));
                Assert.Equal(actual, count);
            }
        }
    }

    [Fact]
    public void ByAttribute_CountsAreCorrect()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);

        foreach (var project in result.Projects)
        {
            foreach (var (attrName, count) in project.ByAttribute)
            {
                var actual = project.Tests.Count(t =>
                    string.Equals(t.AttributeShortName, attrName, StringComparison.Ordinal));
                Assert.Equal(actual, count);
            }
        }
    }

    [Fact]
    public void Tests_SortedByFileLine()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);

        foreach (var project in result.Projects)
        {
            for (int i = 1; i < project.Tests.Count; i++)
            {
                var prev = project.Tests[i - 1];
                var curr = project.Tests[i];
                var fileCmp = string.CompareOrdinal(prev.FilePath, curr.FilePath);
                Assert.True(fileCmp < 0 || (fileCmp == 0 && prev.Line <= curr.Line),
                    $"Sort violation in {project.Project} at index {i}: '{prev.FilePath}:{prev.Line}' before '{curr.FilePath}:{curr.Line}'");
            }
        }
    }

    [Fact]
    public void UnknownProject_ReturnsEmptyList()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: "DoesNotExist");

        Assert.Empty(result.Projects);
    }

    [Fact]
    public void Tests_PopulatedWithMethodNameAndLocation()
    {
        var result = GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);

        Assert.NotEmpty(result.Projects);
        foreach (var project in result.Projects)
        {
            Assert.All(project.Tests, t =>
            {
                Assert.NotEmpty(t.MethodName);
                Assert.NotEmpty(t.Framework);
                Assert.NotEmpty(t.AttributeShortName);
                Assert.NotEmpty(t.FilePath);
                Assert.True(t.Line > 0);
            });
        }
    }
}
```

**Step 2: Run failing**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetTestSummaryToolTests"
```

Expect compile error (`GetTestSummaryLogic` doesn't exist).

**Step 3: Create `src/RoslynCodeLens/Tools/GetTestSummaryLogic.cs`**

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class GetTestSummaryLogic
{
    private static readonly SymbolDisplayFormat FqnFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType);

    public static GetTestSummaryResult Execute(
        LoadedSolution loaded, SymbolResolver resolver, string? project)
    {
        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);
        var entries = new List<ProjectTestSummary>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            if (!testProjectIds.Contains(projectId)) continue;

            var projectName = resolver.GetProjectName(projectId);
            if (project is not null && !string.Equals(projectName, project, StringComparison.OrdinalIgnoreCase))
                continue;

            var tests = new List<TestMethodSummary>();

            foreach (var type in EnumerateTypes(compilation.Assembly.GlobalNamespace))
            {
                if (!type.Locations.Any(l => l.IsInSource)) continue;

                foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
                {
                    var classification = TestMethodClassifier.Classify(member);
                    if (classification is null) continue;

                    var location = member.Locations.FirstOrDefault(l => l.IsInSource);
                    if (location?.SourceTree is null) continue;
                    if (GeneratedCodeDetector.IsGenerated(location.SourceTree)) continue;

                    var (file, line) = GetFileAndLine(member);
                    if (string.IsNullOrEmpty(file)) continue;

                    var rowCount = CountInlineDataRows(member, classification.Framework);
                    var referenced = CollectReferencedSymbols(member, compilation);

                    tests.Add(new TestMethodSummary(
                        MethodName: member.ToDisplayString(FqnFormat).Replace("global::", string.Empty, StringComparison.Ordinal),
                        Framework: classification.Framework.ToString(),
                        AttributeShortName: classification.AttributeShortName,
                        InlineDataRowCount: rowCount,
                        ReferencedSymbols: referenced,
                        FilePath: file,
                        Line: line));
                }
            }

            tests.Sort((a, b) =>
            {
                var fileCmp = string.CompareOrdinal(a.FilePath, b.FilePath);
                return fileCmp != 0 ? fileCmp : a.Line.CompareTo(b.Line);
            });

            var byFramework = tests
                .GroupBy(t => t.Framework, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var byAttribute = tests
                .GroupBy(t => t.AttributeShortName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            entries.Add(new ProjectTestSummary(
                Project: projectName,
                TotalTests: tests.Count,
                ByFramework: byFramework,
                ByAttribute: byAttribute,
                Tests: tests));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Project, b.Project));
        return new GetTestSummaryResult(entries);
    }

    private static int CountInlineDataRows(IMethodSymbol method, TestFramework framework)
    {
        var dataAttributeName = framework switch
        {
            TestFramework.XUnit => "InlineDataAttribute",
            TestFramework.NUnit => "TestCaseAttribute",
            TestFramework.MSTest => "DataRowAttribute",
            _ => null,
        };
        if (dataAttributeName is null) return 0;

        return method.GetAttributes().Count(a =>
            string.Equals(a.AttributeClass?.Name, dataAttributeName, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> CollectReferencedSymbols(IMethodSymbol method, Compilation compilation)
    {
        var location = method.Locations.FirstOrDefault(l => l.IsInSource);
        if (location?.SourceTree is null) return [];

        var semanticModel = compilation.GetSemanticModel(location.SourceTree);
        var bodyNode = location.SourceTree.GetRoot().FindNode(location.SourceSpan);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in bodyNode.DescendantNodesAndSelf())
        {
            var symbol = semanticModel.GetSymbolInfo(node).Symbol;
            if (symbol is null) continue;
            if (symbol is not (IMethodSymbol or IPropertySymbol or IFieldSymbol)) continue;

            var ns = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            // Containing-type's namespace for members:
            if (symbol is not INamedTypeSymbol)
                ns = symbol.ContainingType?.ContainingNamespace?.ToDisplayString() ?? ns;

            if (IsExcludedNamespace(ns)) continue;

            var fqn = symbol.ToDisplayString(FqnFormat).Replace("global::", string.Empty, StringComparison.Ordinal);
            seen.Add(fqn);
        }

        return seen.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static bool IsExcludedNamespace(string ns)
    {
        if (string.IsNullOrEmpty(ns)) return false;

        // Test framework infrastructure — excluded from production-coverage signal.
        if (ns == "Xunit" || ns.StartsWith("Xunit.", StringComparison.Ordinal)) return true;
        if (ns == "NUnit.Framework" || ns.StartsWith("NUnit.Framework.", StringComparison.Ordinal)) return true;
        if (ns == "Microsoft.VisualStudio.TestTools.UnitTesting"
            || ns.StartsWith("Microsoft.VisualStudio.TestTools.UnitTesting.", StringComparison.Ordinal)) return true;

        // BCL — excluded because every test references string/int/Task/etc. NOTE: deliberately
        // narrow under Microsoft.* — only language and runtime support namespaces, NOT consumer
        // frameworks like Microsoft.Extensions.*, Microsoft.AspNetCore.*, Microsoft.EntityFrameworkCore.*
        // which are legitimate production-code references in real test suites.
        if (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)) return true;
        if (ns == "Microsoft.CSharp" || ns.StartsWith("Microsoft.CSharp.", StringComparison.Ordinal)) return true;
        if (ns == "Microsoft.VisualBasic" || ns.StartsWith("Microsoft.VisualBasic.", StringComparison.Ordinal)) return true;
        if (ns == "Microsoft.Win32" || ns.StartsWith("Microsoft.Win32.", StringComparison.Ordinal)) return true;

        return false;
    }

    private static (string File, int Line) GetFileAndLine(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null) return (string.Empty, 0);
        var span = location.GetLineSpan();
        return (span.Path, span.StartLinePosition.Line + 1);
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in EnumerateNestedTypes(type))
                yield return nested;
        }
        foreach (var nested in ns.GetNamespaceMembers())
            foreach (var type in EnumerateTypes(nested))
                yield return type;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in EnumerateNestedTypes(nested))
                yield return deeper;
        }
    }
}
```

**Step 4: Run tests**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetTestSummaryToolTests"
```

Expect 13/13 pass.

**Common debugging:**
- If `Result_FindsXUnitTests` fails: ensure `EnumerateTypes` walks all nested namespaces correctly. If `XUnitFixture` is missing, the project filter is wrong — check `TestProjectDetector.GetTestProjectIds` includes it.
- If `ReferencedSymbols_ExcludesFrameworkAndBcl` fails: the `IsExcludedNamespace` check needs to match all framework/BCL prefixes. Double-check the exact namespaces (some xUnit symbols are in `Xunit.Sdk`, etc.).
- If `InlineDataRowCount_PopulatedForXUnitTheory` reports 0: ensure the attribute-class name comparison matches `"InlineDataAttribute"` exactly (with the `Attribute` suffix, since Roslyn's `AttributeClass.Name` includes it).
- If `Tests_PopulatedWithMethodNameAndLocation` fails on `MethodName`: ensure `FullyQualifiedFormat` strips `global::` correctly.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/GetTestSummaryLogic.cs \
  tests/RoslynCodeLens.Tests/Tools/GetTestSummaryToolTests.cs
git commit -m "feat: add GetTestSummaryLogic with tests"
```

---

## Task 3: MCP wrapper

**Files:**
- Create: `src/RoslynCodeLens/Tools/GetTestSummaryTool.cs`

**Step 1: Create the wrapper**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetTestSummaryTool
{
    [McpServerTool(Name = "get_test_summary")]
    [Description(
        "Per-project inventory of test methods. Each test reports framework " +
        "(xUnit/NUnit/MSTest), attribute kind ([Fact]/[Theory]/[Test]/[TestCase]/" +
        "[TestMethod]/[DataTestMethod]), data-driven row count, location, and the " +
        "production symbols it references. " +
        "Complements find_tests_for_symbol (which goes test → production); this goes " +
        "project → tests. Use to answer 'what does this test suite cover?' or to break " +
        "down test counts by framework/attribute. " +
        "Production projects, generated code, and BCL/framework calls are filtered out " +
        "of the per-test referenced-symbols list. Project filter is case-insensitive. " +
        "Sort: tests by (file, line); projects by name ASC.")]
    public static GetTestSummaryResult Execute(
        MultiSolutionManager manager,
        [Description("Optional: restrict to a single test project by name (case-insensitive).")]
        string? project = null)
    {
        manager.EnsureLoaded();
        return GetTestSummaryLogic.Execute(
            manager.GetLoadedSolution(),
            manager.GetResolver(),
            project);
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
dotnet test tests/RoslynCodeLens.Tests --filter "GetTestSummaryToolTests"
```

Expect 13/13 pass.

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Tools/GetTestSummaryTool.cs
git commit -m "feat: register get_test_summary MCP tool"
```

---

## Task 4: Add benchmark

**Files:**
- Modify: `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

**Step 1**: Find the `find_tests_for_symbol` benchmarks; add the new one immediately after.

```csharp
[Benchmark(Description = "get_test_summary: whole solution")]
public object GetTestSummary()
{
    return GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);
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
git commit -m "bench: add get_test_summary benchmark"
```

---

## Task 5: Update SKILL.md, README, CLAUDE.md

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
- Modify: `README.md`
- Modify: `CLAUDE.md`

**Step 1: SKILL.md — Red Flags routing table**

Add near `find_tests_for_symbol`:

```
| "What does this test suite cover?" / "List all tests in MyProj.Tests" / "How many xUnit Theory tests do we have?" | `get_test_summary` |
```

**Step 2: SKILL.md — Quick Reference**

Add near `find_tests_for_symbol`:

```
| `get_test_summary` | "What does this test suite cover?" |
```

**Step 3: SKILL.md — Test-aware section**

Add a new bullet:

```
- `get_test_summary` — Per-project inventory of test methods with framework, attribute kind, [InlineData]/[TestCase]/[DataRow] row count, location, and production symbols referenced. Project → tests direction; complements `find_tests_for_symbol` (test → production).
```

**Step 4: README.md Features list**

Add near `find_tests_for_symbol`:

```
- **get_test_summary** — Per-project inventory of test methods with framework, attribute kind, data-row count, location, and production symbols referenced
```

**Step 5: CLAUDE.md tool count**

Bump from 34 to 35.

**Step 6: Sanity check**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetTestSummaryToolTests"
```

Expect 13/13 pass.

**Step 7: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md \
  README.md \
  CLAUDE.md
git commit -m "docs: announce get_test_summary in SKILL.md, README, CLAUDE.md"
```

---

## Done

After Task 5 the branch should have ~7 commits (design + plan + 5 tasks). 13/13 tests green, benchmark compiling, tool auto-registered.
