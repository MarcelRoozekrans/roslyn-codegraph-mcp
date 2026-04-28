# `find_uncovered_symbols` Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a new MCP tool `find_uncovered_symbols` that returns public methods + properties no test method transitively reaches (within `maxDepth=3`), sorted by cyclomatic complexity DESC, plus a coverage summary with `riskHotspotCount` (uncovered with complexity ≥ 5).

**Architecture:** Inverted algorithm — walk callees DOWN from every test method once, accumulating reached `IMethodSymbol`s into a `coveredSet`. Then enumerate public methods + properties from non-test projects and diff. Reuses `TestProjectDetector` and `TestAttributeRecognizer` from the prior `find_tests_for_symbol` work. Two small refactors first: extract `TestMethodClassifier` (shared by both test-aware tools) and `ComplexityCalculator` (shared with `get_complexity_metrics`).

**Tech Stack:** Roslyn (`Microsoft.CodeAnalysis`), xUnit, BenchmarkDotNet, ModelContextProtocol.Server.

**Reference design:** `docs/plans/2026-04-28-find-uncovered-symbols-design.md`

**Patterns to mirror (read these before starting):**
- Tool wrapper: `src/RoslynCodeLens/Tools/FindTestsForSymbolTool.cs`
- Logic class: `src/RoslynCodeLens/Tools/FindTestsForSymbolLogic.cs` (especially `ClassifyAsTest` lines 143-173 — being extracted in Task 1)
- Complexity engine: `src/RoslynCodeLens/Tools/GetComplexityMetricsLogic.cs` (especially `CalculateComplexity` lines 49-87 — being extracted in Task 2)
- Test pattern: `tests/RoslynCodeLens.Tests/Tools/FindTestsForSymbolToolTests.cs`
- MCP tool registration: auto-discovered via `WithToolsFromAssembly()` in `src/RoslynCodeLens/Program.cs:35`

---

## Task 1: Extract `TestMethodClassifier`

`FindTestsForSymbolLogic.ClassifyAsTest` is a private helper that determines whether an `IMethodSymbol` is a test method and (if so) which framework + attribute. The new tool needs the same predicate. Extract a shared helper and update the existing call site.

**Files:**
- Create: `src/RoslynCodeLens/TestDiscovery/TestMethodClassifier.cs`
- Modify: `src/RoslynCodeLens/Tools/FindTestsForSymbolLogic.cs` (drop the inline `ClassifyAsTest`, use the new helper)
- Create: `tests/RoslynCodeLens.Tests/TestDiscovery/TestMethodClassifierTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/RoslynCodeLens.Tests/TestDiscovery/TestMethodClassifierTests.cs
using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tests.TestDiscovery;

public class TestMethodClassifierTests : IAsyncLifetime
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
    public void Classify_XUnitFactMethod_ReturnsXUnit()
    {
        var method = FindMethod("XUnitFixture.SampleTests", "DirectGreetTest");
        var classification = TestMethodClassifier.Classify(method);

        Assert.NotNull(classification);
        Assert.Equal(TestFramework.XUnit, classification!.Framework);
        Assert.Equal("Fact", classification.AttributeShortName);
    }

    [Fact]
    public void Classify_NUnitTestMethod_ReturnsNUnit()
    {
        var method = FindMethod("NUnitFixture.SampleTests", "DirectGreetTest");
        var classification = TestMethodClassifier.Classify(method);

        Assert.NotNull(classification);
        Assert.Equal(TestFramework.NUnit, classification!.Framework);
    }

    [Fact]
    public void Classify_MSTestMethod_ReturnsMSTest()
    {
        var method = FindMethod("MSTestFixture.SampleTests", "DirectGreetTest");
        var classification = TestMethodClassifier.Classify(method);

        Assert.NotNull(classification);
        Assert.Equal(TestFramework.MSTest, classification!.Framework);
    }

    [Fact]
    public void Classify_NonTestMethod_ReturnsNull()
    {
        var method = FindMethod("TestLib.Greeter", "Greet");
        Assert.Null(TestMethodClassifier.Classify(method));
    }

    [Fact]
    public void IsTestMethod_TestMethod_ReturnsTrue()
    {
        var method = FindMethod("XUnitFixture.SampleTests", "DirectGreetTest");
        Assert.True(TestMethodClassifier.IsTestMethod(method));
    }

    [Fact]
    public void IsTestMethod_NonTestMethod_ReturnsFalse()
    {
        var method = FindMethod("TestLib.Greeter", "Greet");
        Assert.False(TestMethodClassifier.IsTestMethod(method));
    }

    private IMethodSymbol FindMethod(string typeName, string methodName)
    {
        var methods = _resolver.FindMethods($"{typeName}.{methodName}");
        Assert.NotEmpty(methods);
        return methods[0];
    }
}
```

**Step 2: Run to verify it fails**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "TestMethodClassifierTests" -v normal
```

Expected: compile errors — `TestMethodClassifier` doesn't exist.

**Step 3: Create `TestMethodClassifier.cs`**

```csharp
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.TestDiscovery;

public record TestMethodClassification(TestFramework Framework, string AttributeShortName);

public static class TestMethodClassifier
{
    public static TestMethodClassification? Classify(IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
        {
            var ns = attr.AttributeClass?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            var name = attr.AttributeClass?.Name ?? string.Empty;
            var framework = TestAttributeRecognizer.Recognize(ns, name);
            if (framework is not null)
            {
                var attributeShortName = name.EndsWith("Attribute", StringComparison.Ordinal)
                    ? name[..^"Attribute".Length]
                    : name;
                return new TestMethodClassification(framework.Value, attributeShortName);
            }
        }
        return null;
    }

    public static bool IsTestMethod(IMethodSymbol method) => Classify(method) is not null;
}
```

**Step 4: Update `FindTestsForSymbolLogic.cs`** — replace the inline `ClassifyAsTest` (currently around lines 143-173) so it delegates to `TestMethodClassifier.Classify`:

```csharp
private static TestReference? ClassifyAsTest(IMethodSymbol method, string projectName)
{
    var classification = TestMethodClassifier.Classify(method);
    if (classification is null) return null;

    var location = method.Locations.FirstOrDefault(l => l.IsInSource);
    if (location is null) return null;

    var lineSpan = location.GetLineSpan();

    return new TestReference(
        FullyQualifiedName: method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal),
        Framework: classification.Framework,
        Attribute: classification.AttributeShortName,
        FilePath: lineSpan.Path,
        Line: lineSpan.StartLinePosition.Line + 1,
        Project: projectName);
}
```

**Step 5: Run all tests to verify nothing regressed**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "TestMethodClassifierTests|FindTestsForSymbolToolTests" -v normal
```

Expected: 6 new + 8 existing = 14 tests pass.

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/TestDiscovery/TestMethodClassifier.cs \
  src/RoslynCodeLens/Tools/FindTestsForSymbolLogic.cs \
  tests/RoslynCodeLens.Tests/TestDiscovery/TestMethodClassifierTests.cs
git commit -m "refactor: extract TestMethodClassifier shared helper"
```

---

## Task 2: Extract `ComplexityCalculator`

`GetComplexityMetricsLogic.CalculateComplexity` is private and takes `MethodDeclarationSyntax`. The new tool needs to compute complexity for both methods AND property accessors. Extract to a public static helper and add an overload for any `SyntaxNode` (the algorithm only walks descendants, so it works for accessors too).

**Files:**
- Create: `src/RoslynCodeLens/Analysis/ComplexityCalculator.cs`
- Modify: `src/RoslynCodeLens/Tools/GetComplexityMetricsLogic.cs` (delegate to the new helper)
- Create: `tests/RoslynCodeLens.Tests/Analysis/ComplexityCalculatorTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/RoslynCodeLens.Tests/Analysis/ComplexityCalculatorTests.cs
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;

namespace RoslynCodeLens.Tests.Analysis;

public class ComplexityCalculatorTests
{
    [Fact]
    public void Calculate_TrivialMethod_ReturnsOne()
    {
        var method = ParseMethod("public void M() { return; }");
        Assert.Equal(1, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Calculate_IfStatement_AddsOne()
    {
        var method = ParseMethod("public void M(bool x) { if (x) return; }");
        Assert.Equal(2, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Calculate_NestedIfElseAndLoop_CountsAll()
    {
        var method = ParseMethod(@"
            public int M(int x)
            {
                if (x > 0)
                {
                    for (int i = 0; i < x; i++)
                    {
                        if (i % 2 == 0) return i;
                    }
                }
                else
                {
                    return -1;
                }
                return 0;
            }");
        // base 1 + if + for + nested if + else = 5
        Assert.Equal(5, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Calculate_BooleanShortCircuit_AddsOnePerOperator()
    {
        var method = ParseMethod("public bool M(bool a, bool b, bool c) { return a && b || c; }");
        // base 1 + && + || = 3
        Assert.Equal(3, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Calculate_AccessorDeclaration_WorksOnAccessor()
    {
        var accessor = ParsePropertyGetter(@"
            public int Total
            {
                get
                {
                    if (_x > 0) return _x;
                    return 0;
                }
            }");
        // base 1 + if = 2
        Assert.Equal(2, ComplexityCalculator.Calculate(accessor));
    }

    private static MethodDeclarationSyntax ParseMethod(string code)
    {
        var tree = CSharpSyntaxTree.ParseText($"class C {{ {code} }}");
        return tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
    }

    private static AccessorDeclarationSyntax ParsePropertyGetter(string code)
    {
        var tree = CSharpSyntaxTree.ParseText($"class C {{ {code} }}");
        return tree.GetRoot().DescendantNodes().OfType<AccessorDeclarationSyntax>().First();
    }
}
```

**Step 2: Run to verify it fails**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "ComplexityCalculatorTests" -v normal
```

Expected: compile errors — `ComplexityCalculator` doesn't exist.

**Step 3: Create `ComplexityCalculator.cs`**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynCodeLens.Analysis;

public static class ComplexityCalculator
{
    /// <summary>
    /// Computes McCabe cyclomatic complexity for the given syntax node.
    /// Counts: if/else, switch sections, for/foreach/while/do, catch, conditional expression,
    /// short-circuit operators (&&, ||, ??).
    /// </summary>
    public static int Calculate(SyntaxNode node)
    {
        var complexity = 1;

        foreach (var descendant in node.DescendantNodes())
        {
            switch (descendant.Kind())
            {
                case SyntaxKind.IfStatement:
                case SyntaxKind.ElseClause:
                case SyntaxKind.SwitchSection:
                case SyntaxKind.ForStatement:
                case SyntaxKind.ForEachStatement:
                case SyntaxKind.WhileStatement:
                case SyntaxKind.DoStatement:
                case SyntaxKind.CatchClause:
                case SyntaxKind.ConditionalExpression:
                    complexity++;
                    break;
            }
        }

        foreach (var token in node.DescendantTokens())
        {
#pragma warning disable EPS06
            var kind = token.Kind();
#pragma warning restore EPS06
            switch (kind)
            {
                case SyntaxKind.AmpersandAmpersandToken:
                case SyntaxKind.BarBarToken:
                case SyntaxKind.QuestionQuestionToken:
                    complexity++;
                    break;
            }
        }

        return complexity;
    }
}
```

**Step 4: Update `GetComplexityMetricsLogic.cs`** — replace the private `CalculateComplexity` (lines 49-87) with a delegating call. The remaining body of `Execute` keeps using the same call site:

```csharp
// At line 29, change:
var complexity = CalculateComplexity(method);
// To:
var complexity = ComplexityCalculator.Calculate(method);
```

Then delete the private `CalculateComplexity` method (lines 49-87) entirely. Add `using RoslynCodeLens.Analysis;` to the using list.

**Step 5: Run all tests including the existing complexity tests**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "ComplexityCalculatorTests|GetComplexityMetricsToolTests" -v normal
```

Expected: 5 new + existing complexity tests pass.

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Analysis/ComplexityCalculator.cs \
  src/RoslynCodeLens/Tools/GetComplexityMetricsLogic.cs \
  tests/RoslynCodeLens.Tests/Analysis/ComplexityCalculatorTests.cs
git commit -m "refactor: extract ComplexityCalculator shared helper"
```

---

## Task 3: Add fixture symbols for the new tool

The fixture solution already has the `Greeter.GreetFormal` uncovered method. Add a few more so the tests for the new tool can verify property kind, complexity attribution, and risk-hotspot detection.

**Files:**
- Modify: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/Greeter.cs`

**Step 1: Read the existing file**

Use the Read tool to inspect the current state. Preserve all existing methods/properties.

**Step 2: Append the following to `Greeter` class** (preserve all existing members; add inside the class):

```csharp
// Public computed property — uncovered. Complexity > 1 because of the if branch.
public int FormalNameLength
{
    get
    {
        if (_lastFormalName is null)
            return 0;
        return _lastFormalName.Length;
    }
}

private string? _lastFormalName;

// Public method — uncovered, high complexity (>= 5) for the riskHotspotCount test.
public string ClassifyName(string name)
{
    if (string.IsNullOrEmpty(name))
        return "empty";
    if (name.Length < 3)
        return "short";
    if (name.Length > 20)
        return "long";
    if (name.Contains(' '))
        return "multi-word";
    return "normal";
}
```

This adds:
- `FormalNameLength` — `kind: property`, complexity 2 (one `if`)
- `ClassifyName` — `kind: method`, complexity 6 (five `if` branches + base 1) → above the riskThreshold of 5
- `_lastFormalName` — private field, irrelevant to coverage

**Step 3: Build to confirm**

```bash
dotnet build tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestSolution.slnx -c Debug
```

Expected: 0 errors.

**Step 4: Run the existing test suite to confirm no regressions**

```bash
dotnet test tests/RoslynCodeLens.Tests
```

Expected: same pass count as before. (The only existing test that targets `Greeter.GreetFormal` is `Direct_SymbolWithZeroTestCallers_ReturnsEmpty` from the prior find_tests_for_symbol PR; adding more uncovered symbols won't break it.)

**Step 5: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/Greeter.cs
git commit -m "test: add fixture symbols for find_uncovered_symbols (computed property + high-complexity method)"
```

---

## Task 4: Models for the uncovered-symbols output

Three records + one enum. Pure data shapes.

**Files:**
- Create: `src/RoslynCodeLens/Models/UncoveredSymbolKind.cs`
- Create: `src/RoslynCodeLens/Models/UncoveredSymbol.cs`
- Create: `src/RoslynCodeLens/Models/CoverageSummary.cs`
- Create: `src/RoslynCodeLens/Models/FindUncoveredSymbolsResult.cs`

**Step 1: Create `UncoveredSymbolKind.cs`**

```csharp
namespace RoslynCodeLens.Models;

public enum UncoveredSymbolKind
{
    Method,
    Property
}
```

**Step 2: Create `UncoveredSymbol.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record UncoveredSymbol(
    string Symbol,
    UncoveredSymbolKind Kind,
    string FilePath,
    int Line,
    string Project,
    int Complexity);
```

**Step 3: Create `CoverageSummary.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record CoverageSummary(
    int TotalSymbols,
    int CoveredCount,
    int UncoveredCount,
    int CoveragePercent,
    int RiskHotspotCount);
```

**Step 4: Create `FindUncoveredSymbolsResult.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record FindUncoveredSymbolsResult(
    CoverageSummary Summary,
    IReadOnlyList<UncoveredSymbol> UncoveredSymbols);
```

**Step 5: Build**

```bash
dotnet build src/RoslynCodeLens
```

Expected: 0 errors.

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Models/UncoveredSymbolKind.cs \
  src/RoslynCodeLens/Models/UncoveredSymbol.cs \
  src/RoslynCodeLens/Models/CoverageSummary.cs \
  src/RoslynCodeLens/Models/FindUncoveredSymbolsResult.cs
git commit -m "feat: add models for find_uncovered_symbols output"
```

---

## Task 5: `FindUncoveredSymbolsLogic` — TDD with multiple test cases

The core engine: walk callees down from each test, build covered set, enumerate candidates, diff, sort, summarise.

**Files:**
- Create: `src/RoslynCodeLens/Tools/FindUncoveredSymbolsLogic.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/FindUncoveredSymbolsToolTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/RoslynCodeLens.Tests/Tools/FindUncoveredSymbolsToolTests.cs
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindUncoveredSymbolsToolTests : IAsyncLifetime
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
    public void Result_GreetFormal_AppearsAsUncovered()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        Assert.Contains(result.UncoveredSymbols, s =>
            s.Symbol.EndsWith("GreetFormal", StringComparison.Ordinal) &&
            s.Kind == UncoveredSymbolKind.Method);
    }

    [Fact]
    public void Result_Greet_DoesNotAppearAsUncovered()
    {
        // Greet is called by tests in all three frameworks — must be covered.
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.UncoveredSymbols, s =>
            s.Symbol.EndsWith("Greeter.Greet", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_FormalNameLength_AppearsAsProperty()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        var symbol = Assert.Single(result.UncoveredSymbols, s =>
            s.Symbol.EndsWith("FormalNameLength", StringComparison.Ordinal));
        Assert.Equal(UncoveredSymbolKind.Property, symbol.Kind);
        Assert.Equal(2, symbol.Complexity);  // base 1 + one if
    }

    [Fact]
    public void Result_ClassifyName_HasHighComplexity()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        var symbol = Assert.Single(result.UncoveredSymbols, s =>
            s.Symbol.EndsWith("ClassifyName", StringComparison.Ordinal));
        Assert.True(symbol.Complexity >= 5,
            $"ClassifyName should have complexity >= 5; was {symbol.Complexity}");
    }

    [Fact]
    public void Result_Summary_CountsAddUp()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        var s = result.Summary;
        Assert.Equal(s.TotalSymbols, s.CoveredCount + s.UncoveredCount);
        Assert.Equal(s.UncoveredCount, result.UncoveredSymbols.Count);
        Assert.InRange(s.CoveragePercent, 0, 100);
    }

    [Fact]
    public void Result_Summary_RiskHotspotCount_CountsHighComplexityUncovered()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        var actualHotspots = result.UncoveredSymbols.Count(s => s.Complexity >= 5);
        Assert.Equal(actualHotspots, result.Summary.RiskHotspotCount);
        Assert.True(result.Summary.RiskHotspotCount >= 1,
            "ClassifyName alone should make this >= 1");
    }

    [Fact]
    public void Result_UncoveredSymbols_SortedByComplexityDescending()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        for (int i = 1; i < result.UncoveredSymbols.Count; i++)
        {
            Assert.True(
                result.UncoveredSymbols[i - 1].Complexity >= result.UncoveredSymbols[i].Complexity,
                $"Sort violation at index {i}: {result.UncoveredSymbols[i - 1].Complexity} < {result.UncoveredSymbols[i].Complexity}");
        }
    }

    [Fact]
    public void Result_UncoveredSymbols_HaveLocationInfo()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        foreach (var s in result.UncoveredSymbols)
        {
            Assert.NotEmpty(s.FilePath);
            Assert.True(s.Line > 0);
            Assert.NotEmpty(s.Project);
        }
    }

    [Fact]
    public void Result_DoesNotIncludeTestProjectMembers()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        // No member of the test fixture projects should appear in uncovered.
        Assert.DoesNotContain(result.UncoveredSymbols, s =>
            s.Project.EndsWith("Fixture", StringComparison.Ordinal));
    }
}
```

**Step 2: Run to verify they fail**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindUncoveredSymbolsToolTests" -v normal
```

Expected: compile error — `FindUncoveredSymbolsLogic` doesn't exist.

**Step 3: Create `FindUncoveredSymbolsLogic.cs`**

```csharp
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class FindUncoveredSymbolsLogic
{
    private const int MaxDepth = 3;
    private const int RiskThreshold = 5;

    public static FindUncoveredSymbolsResult Execute(LoadedSolution loaded, SymbolResolver source)
    {
        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);

        // 1. Walk callees DOWN from every test method to build the covered set.
        var coveredSet = BuildCoveredSet(loaded, testProjectIds);

        // 2. Enumerate candidates: public methods + properties from non-test projects.
        var candidates = EnumerateCandidates(loaded, source, testProjectIds);

        // 3. Diff and build output.
        var uncovered = new List<UncoveredSymbol>();
        var coveredCount = 0;
        foreach (var (symbol, projectName) in candidates)
        {
            if (IsCovered(symbol, coveredSet))
                coveredCount++;
            else
                uncovered.Add(BuildUncoveredSymbol(symbol, projectName));
        }

        // 4. Sort: complexity DESC, then symbol name ASC.
        uncovered.Sort((a, b) =>
        {
            var byComplexity = b.Complexity.CompareTo(a.Complexity);
            return byComplexity != 0
                ? byComplexity
                : string.CompareOrdinal(a.Symbol, b.Symbol);
        });

        // 5. Summary.
        var total = candidates.Count;
        var coveragePercent = total == 0
            ? 100
            : (int)Math.Floor((double)coveredCount / total * 100);
        var riskHotspotCount = uncovered.Count(s => s.Complexity >= RiskThreshold);
        var summary = new CoverageSummary(
            TotalSymbols: total,
            CoveredCount: coveredCount,
            UncoveredCount: uncovered.Count,
            CoveragePercent: coveragePercent,
            RiskHotspotCount: riskHotspotCount);

        return new FindUncoveredSymbolsResult(summary, uncovered);
    }

    private static HashSet<IMethodSymbol> BuildCoveredSet(
        LoadedSolution loaded,
        ImmutableHashSet<ProjectId> testProjectIds)
    {
        var covered = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<(IMethodSymbol Method, int Depth)>();

        // Seed: every test method, depth 0.
        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            if (!testProjectIds.Contains(projectId))
                continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();
                foreach (var methodDecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (semanticModel.GetDeclaredSymbol(methodDecl) is not IMethodSymbol method)
                        continue;
                    if (!TestMethodClassifier.IsTestMethod(method))
                        continue;
                    if (visited.Add(method))
                        queue.Enqueue((method, 0));
                }
            }
        }

        // BFS down through callees up to MaxDepth.
        while (queue.Count > 0)
        {
            var (frontier, depth) = queue.Dequeue();
            if (depth >= MaxDepth)
                continue;

            foreach (var callee in EnumerateCallees(frontier, loaded))
            {
                var original = callee.OriginalDefinition;
                if (!visited.Add(original))
                    continue;
                covered.Add(original);
                queue.Enqueue((original, depth + 1));
            }
        }

        return covered;
    }

    private static IEnumerable<IMethodSymbol> EnumerateCallees(IMethodSymbol method, LoadedSolution loaded)
    {
        foreach (var location in method.Locations)
        {
            if (!location.IsInSource)
                continue;
            var tree = location.SourceTree;
            if (tree is null)
                continue;

            // Find the compilation that owns this tree.
            Compilation? compilation = null;
            foreach (var (_, comp) in loaded.Compilations)
            {
                if (comp.SyntaxTrees.Contains(tree))
                {
                    compilation = comp;
                    break;
                }
            }
            if (compilation is null)
                continue;

            var semanticModel = compilation.GetSemanticModel(tree);
            var declNode = tree.GetRoot().FindNode(location.SourceSpan);

            // Method invocations.
            foreach (var invocation in declNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (semanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol called)
                    yield return called;
            }

            // Property accesses — return both accessors so either reading or writing the
            // property covers it.
            foreach (var memberAccess in declNode.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (semanticModel.GetSymbolInfo(memberAccess).Symbol is IPropertySymbol prop)
                {
                    if (prop.GetMethod is not null)
                        yield return prop.GetMethod;
                    if (prop.SetMethod is not null)
                        yield return prop.SetMethod;
                }
            }
        }
    }

    private record CandidateInfo(ISymbol Symbol, string ProjectName);

    private static IReadOnlyList<CandidateInfo> EnumerateCandidates(
        LoadedSolution loaded,
        SymbolResolver source,
        ImmutableHashSet<ProjectId> testProjectIds)
    {
        var candidates = new List<CandidateInfo>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            if (testProjectIds.Contains(projectId))
                continue;

            var projectName = source.GetProjectName(projectId);

            foreach (var type in EnumerateTypes(compilation.Assembly.GlobalNamespace))
            {
                if (type.DeclaredAccessibility != Accessibility.Public)
                    continue;
                if (!type.Locations.Any(l => l.IsInSource))
                    continue;

                foreach (var member in type.GetMembers())
                {
                    if (member.DeclaredAccessibility != Accessibility.Public)
                        continue;
                    if (!member.Locations.Any(l => l.IsInSource))
                        continue;
                    if (member.IsImplicitlyDeclared)
                        continue;

                    if (member is IMethodSymbol method)
                    {
                        if (method.MethodKind != MethodKind.Ordinary)
                            continue;
                        if (method.IsAbstract)
                            continue;
                        candidates.Add(new CandidateInfo(method, projectName));
                    }
                    else if (member is IPropertySymbol property)
                    {
                        if (property.IsAbstract)
                            continue;
                        if (property.GetMethod is null && property.SetMethod is null)
                            continue;
                        candidates.Add(new CandidateInfo(property, projectName));
                    }
                }
            }
        }

        return candidates;
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

    private static bool IsCovered(ISymbol candidate, HashSet<IMethodSymbol> coveredSet)
    {
        if (candidate is IMethodSymbol method)
            return coveredSet.Contains(method.OriginalDefinition);
        if (candidate is IPropertySymbol property)
            return (property.GetMethod is not null && coveredSet.Contains(property.GetMethod.OriginalDefinition))
                || (property.SetMethod is not null && coveredSet.Contains(property.SetMethod.OriginalDefinition));
        return false;
    }

    private static UncoveredSymbol BuildUncoveredSymbol(ISymbol symbol, string projectName)
    {
        var location = symbol.Locations.First(l => l.IsInSource);
        var lineSpan = location.GetLineSpan();
        var kind = symbol is IMethodSymbol ? UncoveredSymbolKind.Method : UncoveredSymbolKind.Property;
        var symbolName = symbol.ContainingType is null
            ? symbol.Name
            : $"{symbol.ContainingType.Name}.{symbol.Name}";
        var complexity = ComputeComplexity(symbol);

        return new UncoveredSymbol(
            Symbol: symbolName,
            Kind: kind,
            FilePath: lineSpan.Path,
            Line: lineSpan.StartLinePosition.Line + 1,
            Project: projectName,
            Complexity: complexity);
    }

    private static int ComputeComplexity(ISymbol symbol)
    {
        var maxComplexity = 1;
        foreach (var location in symbol.Locations)
        {
            if (!location.IsInSource)
                continue;
            var tree = location.SourceTree;
            if (tree is null)
                continue;

            var node = tree.GetRoot().FindNode(location.SourceSpan);

            if (node is MethodDeclarationSyntax)
            {
                maxComplexity = Math.Max(maxComplexity, ComplexityCalculator.Calculate(node));
            }
            else if (node is PropertyDeclarationSyntax property)
            {
                if (property.AccessorList is not null)
                {
                    foreach (var accessor in property.AccessorList.Accessors)
                        maxComplexity = Math.Max(maxComplexity, ComplexityCalculator.Calculate(accessor));
                }
                else if (property.ExpressionBody is not null)
                {
                    maxComplexity = Math.Max(maxComplexity, ComplexityCalculator.Calculate(property.ExpressionBody));
                }
            }
        }
        return maxComplexity;
    }
}
```

**Step 4: Run all 9 tests; iterate if any fail**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindUncoveredSymbolsToolTests" -v normal
```

Expected: 9 tests pass.

If a test fails, the most likely causes:
- Wrong complexity attribution: re-check `ComputeComplexity` — a `PropertyDeclarationSyntax` with a body uses `AccessorList`; an expression-bodied property uses `ExpressionBody`.
- Test project members appearing as uncovered: ensure `EnumerateCandidates` correctly skips test projects.
- Symbols not found: verify `OriginalDefinition` is used consistently in both the BFS and the diff.

**Step 5: Run the full test suite to ensure no regressions**

```bash
dotnet test
```

Expected: all tests pass (9 new + everything previously green).

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Tools/FindUncoveredSymbolsLogic.cs \
  tests/RoslynCodeLens.Tests/Tools/FindUncoveredSymbolsToolTests.cs
git commit -m "feat: add FindUncoveredSymbolsLogic"
```

---

## Task 6: `FindUncoveredSymbolsTool` MCP wrapper

Thin MCP-attribute wrapper. Auto-registered via `WithToolsFromAssembly()`.

**Files:**
- Create: `src/RoslynCodeLens/Tools/FindUncoveredSymbolsTool.cs`

**Step 1: Create `FindUncoveredSymbolsTool.cs`**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindUncoveredSymbolsTool
{
    [McpServerTool(Name = "find_uncovered_symbols")]
    [Description(
        "Report public methods and properties that no test method transitively reaches " +
        "(within 3 helper hops). Output sorted by cyclomatic complexity descending, " +
        "with a coverage summary including a riskHotspotCount (uncovered with " +
        "complexity >= 5). Recognises xUnit, NUnit, MSTest. Reference-based static " +
        "analysis — does not parse runtime coverage data.")]
    public static FindUncoveredSymbolsResult Execute(MultiSolutionManager manager)
    {
        manager.EnsureLoaded();
        return FindUncoveredSymbolsLogic.Execute(
            manager.GetLoadedSolution(),
            manager.GetResolver());
    }
}
```

**Step 2: Build the whole solution**

```bash
dotnet build
```

Expected: 0 errors. Auto-registration via `Program.cs:35` picks up the new `[McpServerToolType]`.

**Step 3: Run the full test suite**

```bash
dotnet test
```

Expected: all green.

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Tools/FindUncoveredSymbolsTool.cs
git commit -m "feat: register find_uncovered_symbols MCP tool"
```

---

## Task 7: Add benchmark

**Files:**
- Modify: `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

**Step 1: Read the file** to confirm fields (`_loaded`, `_resolver`, etc.) and find a sensible spot near the other `find_*` benchmarks.

**Step 2: Add the benchmark method** alongside the existing ones:

```csharp
[Benchmark(Description = "find_uncovered_symbols: whole solution")]
public object FindUncoveredSymbols()
{
    return FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);
}
```

**Step 3: Build the benchmarks project**

```bash
dotnet build benchmarks/RoslynCodeLens.Benchmarks -c Release
```

Expected: 0 errors.

**Step 4: Commit**

```bash
git add benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs
git commit -m "bench: add find_uncovered_symbols benchmark"
```

---

## Task 8: Update SKILL.md, README, CLAUDE.md

Make agents and users aware of the new tool. Update tool counts.

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
- Modify: `README.md`
- Modify: `CLAUDE.md`

**Step 1: Read `SKILL.md`** and add the tool to its various tool-cataloging structures (Red Flags table, Finding Dependencies and Usage section, Quick Reference table). Use this routing line:

> | "What should I write tests for?" / "Where's our testing debt?" / "Show me untested public methods" | `find_uncovered_symbols` |

For the Quick Reference table:

> | `find_uncovered_symbols` | "What should I write tests for?" / "Where's our testing debt?" |

For the Finding Dependencies and Usage section, insert near `find_tests_for_symbol`:

> - `find_uncovered_symbols` — Public methods and properties no test transitively reaches (≤ 3 helper hops); sorted by cyclomatic complexity for prioritization.

Note: do NOT add a metadata-support row — this tool only operates on source.

**Step 2: Read `README.md`** and add to the Features list near `find_tests_for_symbol`:

> - **find_uncovered_symbols** — Public methods and properties no test transitively reaches; sorted by cyclomatic complexity for prioritization.

**Step 3: Update `CLAUDE.md`** — change "23 code intelligence tools" to "24 code intelligence tools".

**Step 4: Sanity check**

```bash
dotnet test
```

Expected: all green.

**Step 5: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md \
  README.md \
  CLAUDE.md
git commit -m "docs: announce find_uncovered_symbols in SKILL.md, README, CLAUDE.md"
```

---

## Done

After Task 8 the branch should have ~9 commits, all tests green, the benchmark project compiling, and the tool auto-registered. From there: code review (`/requesting-code-review`), then PR.
