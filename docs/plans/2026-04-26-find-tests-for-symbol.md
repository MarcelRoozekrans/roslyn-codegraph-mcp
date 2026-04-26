# `find_tests_for_symbol` Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a new MCP tool `find_tests_for_symbol` that lists test methods exercising a given production symbol across xUnit, NUnit, and MSTest. Default mode returns direct test callers; opt-in `transitive: true` walks helpers up to a bounded depth.

**Architecture:** Pure attribute recogniser (no Roslyn deps) + package-ref-based test-project detector + a new logic class that walks call sites (similar pattern to `FindCallersLogic`), classifies each caller as test-or-not, and BFS-walks helpers in transitive mode with a visited set.

**Tech Stack:** Roslyn (`Microsoft.CodeAnalysis`), xUnit for unit tests, BenchmarkDotNet, ModelContextProtocol.Server.

**Reference design:** `docs/plans/2026-04-26-find-tests-for-symbol-design.md`

**Patterns to mirror (read these before starting):**
- Tool wrapper: `src/RoslynCodeLens/Tools/FindCallersTool.cs`
- Logic class: `src/RoslynCodeLens/Tools/FindCallersLogic.cs`
- Test pattern: `tests/RoslynCodeLens.Tests/Tools/FindCallersToolTests.cs`
- Tool registration: `src/RoslynCodeLens/Program.cs` (look for `WithTools` / how existing tools are wired)

---

## Task 1: Add NUnit + MSTest fixture projects

The existing fixture solution has only xUnit projects. We need a tiny NUnit and a tiny MSTest fixture project so the tool's tests can verify multi-framework recognition. Both fixtures reference `TestLib` so they call `Greeter.Greet` — the symbol our tool tests will target.

**Files:**
- Create: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/NUnitFixture/NUnitFixture.csproj`
- Create: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/NUnitFixture/SampleTests.cs`
- Create: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/MSTestFixture/MSTestFixture.csproj`
- Create: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/MSTestFixture/SampleTests.cs`
- Modify: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestSolution.slnx`

**Step 1: Create `NUnitFixture/NUnitFixture.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\TestLib\TestLib.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="NUnit" Version="4.2.2" />
  </ItemGroup>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

**Step 2: Create `NUnitFixture/SampleTests.cs`**

```csharp
using NUnit.Framework;
using TestLib;

namespace NUnitFixture;

[TestFixture]
public class SampleTests
{
    [Test]
    public void DirectGreetTest()
    {
        var greeter = new Greeter();
        var result = greeter.Greet("world");
        Assert.That(result, Is.Not.Null);
    }

    [TestCase("alice")]
    [TestCase("bob")]
    public void ParameterisedGreetTest(string name)
    {
        var greeter = new Greeter();
        var result = greeter.Greet(name);
        Assert.That(result, Is.Not.Null);
    }

    private static void HelperThatGreets(string name)
    {
        var greeter = new Greeter();
        greeter.Greet(name);
    }

    [Test]
    public void TransitiveGreetTest()
    {
        HelperThatGreets("via helper");
    }
}
```

**Step 3: Create `MSTestFixture/MSTestFixture.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\TestLib\TestLib.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="MSTest.TestFramework" Version="3.6.4" />
  </ItemGroup>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

**Step 4: Create `MSTestFixture/SampleTests.cs`**

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestLib;

namespace MSTestFixture;

[TestClass]
public class SampleTests
{
    [TestMethod]
    public void DirectGreetTest()
    {
        var greeter = new Greeter();
        var result = greeter.Greet("world");
        Assert.IsNotNull(result);
    }

    [DataTestMethod]
    [DataRow("alice")]
    public void ParameterisedGreetTest(string name)
    {
        var greeter = new Greeter();
        var result = greeter.Greet(name);
        Assert.IsNotNull(result);
    }
}
```

**Step 5: Update `TestSolution.slnx`** — add the two new projects:

```xml
<Solution>
  <Project Path="TestLib/TestLib.csproj" />
  <Project Path="TestLib2/TestLib2.csproj" />
  <Project Path="NUnitFixture/NUnitFixture.csproj" />
  <Project Path="MSTestFixture/MSTestFixture.csproj" />
</Solution>
```

(Read the file first to preserve any other projects already listed.)

**Step 6: Restore + build the fixture solution**

```bash
dotnet restore tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestSolution.slnx
dotnet build tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestSolution.slnx -c Debug
```

Expected: Build succeeded, 0 errors. (Some `NU1903` package-vulnerability warnings may show — those are expected and unrelated.)

**Step 7: Run the existing test suite to ensure nothing breaks**

```bash
dotnet test tests/RoslynCodeLens.Tests
```

Expected: same pass/fail count as before this task. Existing tests load the fixture and may iterate projects — adding projects should not break them.

**Step 8: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Fixtures/TestSolution/
git commit -m "test: add NUnit and MSTest fixture projects"
```

---

## Task 2: TestFramework enum + TestAttributeRecognizer

Pure data — given `(namespaceFullName, attributeName)`, returns which test framework owns it (or null). No Roslyn dependency, trivially testable.

**Files:**
- Create: `src/RoslynCodeLens/TestDiscovery/TestFramework.cs`
- Create: `src/RoslynCodeLens/TestDiscovery/TestAttributeRecognizer.cs`
- Create: `tests/RoslynCodeLens.Tests/TestDiscovery/TestAttributeRecognizerTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/RoslynCodeLens.Tests/TestDiscovery/TestAttributeRecognizerTests.cs
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tests.TestDiscovery;

public class TestAttributeRecognizerTests
{
    [Theory]
    [InlineData("Xunit", "FactAttribute", TestFramework.XUnit)]
    [InlineData("Xunit", "TheoryAttribute", TestFramework.XUnit)]
    [InlineData("NUnit.Framework", "TestAttribute", TestFramework.NUnit)]
    [InlineData("NUnit.Framework", "TestCaseAttribute", TestFramework.NUnit)]
    [InlineData("NUnit.Framework", "TestCaseSourceAttribute", TestFramework.NUnit)]
    [InlineData("Microsoft.VisualStudio.TestTools.UnitTesting", "TestMethodAttribute", TestFramework.MSTest)]
    [InlineData("Microsoft.VisualStudio.TestTools.UnitTesting", "DataTestMethodAttribute", TestFramework.MSTest)]
    public void Recognize_KnownAttribute_ReturnsFramework(string ns, string name, TestFramework expected)
    {
        Assert.Equal(expected, TestAttributeRecognizer.Recognize(ns, name));
    }

    [Theory]
    [InlineData("System", "ObsoleteAttribute")]
    [InlineData("Xunit", "FactSomethingElse")]
    [InlineData("NotARealNamespace", "TestAttribute")]
    public void Recognize_UnknownAttribute_ReturnsNull(string ns, string name)
    {
        Assert.Null(TestAttributeRecognizer.Recognize(ns, name));
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "TestAttributeRecognizerTests" -v normal
```

Expected: FAIL with compile error — `TestAttributeRecognizer` does not exist.

**Step 3: Create `TestFramework.cs`**

```csharp
namespace RoslynCodeLens.TestDiscovery;

public enum TestFramework
{
    XUnit,
    NUnit,
    MSTest
}
```

**Step 4: Create `TestAttributeRecognizer.cs`**

```csharp
namespace RoslynCodeLens.TestDiscovery;

public static class TestAttributeRecognizer
{
    private static readonly Dictionary<(string Namespace, string Name), TestFramework> Map = new()
    {
        [("Xunit", "FactAttribute")] = TestFramework.XUnit,
        [("Xunit", "TheoryAttribute")] = TestFramework.XUnit,
        [("NUnit.Framework", "TestAttribute")] = TestFramework.NUnit,
        [("NUnit.Framework", "TestCaseAttribute")] = TestFramework.NUnit,
        [("NUnit.Framework", "TestCaseSourceAttribute")] = TestFramework.NUnit,
        [("Microsoft.VisualStudio.TestTools.UnitTesting", "TestMethodAttribute")] = TestFramework.MSTest,
        [("Microsoft.VisualStudio.TestTools.UnitTesting", "DataTestMethodAttribute")] = TestFramework.MSTest,
    };

    public static TestFramework? Recognize(string namespaceFullName, string attributeName)
        => Map.TryGetValue((namespaceFullName, attributeName), out var framework) ? framework : null;
}
```

**Step 5: Run test to verify it passes**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "TestAttributeRecognizerTests" -v normal
```

Expected: all 10 tests pass.

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/TestDiscovery/ tests/RoslynCodeLens.Tests/TestDiscovery/TestAttributeRecognizerTests.cs
git commit -m "feat: add TestAttributeRecognizer for xUnit/NUnit/MSTest"
```

---

## Task 3: TestProjectDetector

Detects which projects in the loaded solution are "test projects" by scanning their NuGet package references for `xunit*`, `NUnit*`, or `MSTest*`. Returns an `ImmutableHashSet<ProjectId>`.

**Files:**
- Create: `src/RoslynCodeLens/TestDiscovery/TestProjectDetector.cs`
- Create: `tests/RoslynCodeLens.Tests/TestDiscovery/TestProjectDetectorTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/RoslynCodeLens.Tests/TestDiscovery/TestProjectDetectorTests.cs
using RoslynCodeLens;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tests.TestDiscovery;

public class TestProjectDetectorTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void GetTestProjectIds_DetectsXUnitNUnitAndMSTest()
    {
        var ids = TestProjectDetector.GetTestProjectIds(_loaded.Solution);

        var names = ids
            .Select(id => _loaded.Solution.GetProject(id)!.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Contains("NUnitFixture", names);
        Assert.Contains("MSTestFixture", names);
    }

    [Fact]
    public void GetTestProjectIds_ExcludesNonTestProjects()
    {
        var ids = TestProjectDetector.GetTestProjectIds(_loaded.Solution);

        var names = ids
            .Select(id => _loaded.Solution.GetProject(id)!.Name)
            .ToList();

        Assert.DoesNotContain("TestLib", names);
        Assert.DoesNotContain("TestLib2", names);
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "TestProjectDetectorTests" -v normal
```

Expected: FAIL with compile error — `TestProjectDetector` does not exist.

**Step 3: Create `TestProjectDetector.cs`**

```csharp
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.TestDiscovery;

public static class TestProjectDetector
{
    private static readonly string[] TestPackagePrefixes = ["xunit", "nunit", "mstest"];

    public static ImmutableHashSet<ProjectId> GetTestProjectIds(Solution solution)
    {
        var builder = ImmutableHashSet.CreateBuilder<ProjectId>();

        foreach (var project in solution.Projects)
        {
            if (HasTestPackageReference(project))
                builder.Add(project.Id);
        }

        return builder.ToImmutable();
    }

    private static bool HasTestPackageReference(Project project)
    {
        if (project.FilePath is null || !File.Exists(project.FilePath))
            return false;

        var content = File.ReadAllText(project.FilePath);

        // Look for <PackageReference Include="xunit..." or "NUnit..." or "MSTest..."
        foreach (var prefix in TestPackagePrefixes)
        {
            var needle = $"PackageReference Include=\"{prefix}";
            if (content.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
```

**Why read the csproj directly?** Roslyn's `Project.MetadataReferences` give resolved DLL paths, not the original NuGet identity. The csproj file is the source of truth for declared package references.

**Step 4: Run test to verify it passes**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "TestProjectDetectorTests" -v normal
```

Expected: both tests pass.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/TestDiscovery/TestProjectDetector.cs tests/RoslynCodeLens.Tests/TestDiscovery/TestProjectDetectorTests.cs
git commit -m "feat: add TestProjectDetector via package-ref pattern scan"
```

---

## Task 4: TestReference model + FindTestsForSymbolLogic (direct mode)

The output record + the logic that walks the call sites of the target symbol, classifies each caller as a test method or not, and returns direct hits.

**Files:**
- Create: `src/RoslynCodeLens/Models/TestReference.cs`
- Create: `src/RoslynCodeLens/Models/FindTestsForSymbolResult.cs`
- Create: `src/RoslynCodeLens/Tools/FindTestsForSymbolLogic.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/FindTestsForSymbolToolTests.cs`

**Step 1: Write the failing tests (direct mode only)**

```csharp
// tests/RoslynCodeLens.Tests/Tools/FindTestsForSymbolToolTests.cs
using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindTestsForSymbolToolTests : IAsyncLifetime
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
    public void Direct_FindsXUnitTestsForGreeter()
    {
        var result = FindTestsForSymbolLogic.Execute(
            _loaded, _resolver, _metadata, "Greeter.Greet", transitive: false, maxDepth: 3);

        // The existing xUnit fixture project (TestLib2 / its tests) plus our NUnit fixture
        // call Greeter.Greet directly. Verify that NUnit and MSTest direct hits appear.
        Assert.Contains(result.DirectTests, t => t.Framework == TestFramework.NUnit);
        Assert.Contains(result.DirectTests, t => t.Framework == TestFramework.MSTest);
    }

    [Fact]
    public void Direct_DoesNotIncludeTransitiveCallers()
    {
        var result = FindTestsForSymbolLogic.Execute(
            _loaded, _resolver, _metadata, "Greeter.Greet", transitive: false, maxDepth: 3);

        // TransitiveGreetTest (NUnit) reaches Greet only via HelperThatGreets — it must
        // NOT appear in direct mode.
        Assert.DoesNotContain(result.DirectTests, t =>
            t.FullyQualifiedName.Contains("TransitiveGreetTest", StringComparison.Ordinal));

        Assert.Empty(result.TransitiveTests);
    }

    [Fact]
    public void Direct_IncludesEachAttributeOnceEvenForDataDriven()
    {
        var result = FindTestsForSymbolLogic.Execute(
            _loaded, _resolver, _metadata, "Greeter.Greet", transitive: false, maxDepth: 3);

        // ParameterisedGreetTest exists in both NUnit and MSTest fixtures with multiple
        // data rows; it should appear once per framework, not per row.
        var nunitParameterised = result.DirectTests.Where(t =>
            t.Framework == TestFramework.NUnit &&
            t.FullyQualifiedName.EndsWith("ParameterisedGreetTest", StringComparison.Ordinal)).ToList();
        Assert.Single(nunitParameterised);
    }

    [Fact]
    public void Direct_UnknownSymbol_ReturnsEmpty()
    {
        var result = FindTestsForSymbolLogic.Execute(
            _loaded, _resolver, _metadata, "DoesNotExist.Method", transitive: false, maxDepth: 3);

        Assert.Empty(result.DirectTests);
        Assert.Empty(result.TransitiveTests);
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindTestsForSymbolToolTests" -v normal
```

Expected: FAIL with compile error — `FindTestsForSymbolLogic` and models don't exist.

**Step 3: Create `Models/TestReference.cs`**

```csharp
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Models;

public record TestReference(
    string FullyQualifiedName,
    TestFramework Framework,
    string Attribute,
    string FilePath,
    int Line,
    string Project,
    IReadOnlyList<string>? CallChain = null);
```

`CallChain` is `null` for direct hits, populated only for transitive hits.

**Step 4: Create `Models/FindTestsForSymbolResult.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record FindTestsForSymbolResult(
    string Symbol,
    IReadOnlyList<TestReference> DirectTests,
    IReadOnlyList<TestReference> TransitiveTests);
```

**Step 5: Create `Tools/FindTestsForSymbolLogic.cs` (direct mode only — transitive comes in Task 5)**

Read `src/RoslynCodeLens/Tools/FindCallersLogic.cs` first to understand the call-site walk pattern.

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class FindTestsForSymbolLogic
{
    public static FindTestsForSymbolResult Execute(
        LoadedSolution loaded,
        SymbolResolver source,
        MetadataSymbolResolver metadata,
        string symbol,
        bool transitive = false,
        int maxDepth = 3)
    {
        // Clamp maxDepth into [1, 5]
        maxDepth = Math.Clamp(maxDepth, 1, 5);

        var targetMethods = source.FindMethods(symbol);
        if (targetMethods.Count == 0)
            return new FindTestsForSymbolResult(symbol, [], []);

        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);
        if (testProjectIds.IsEmpty)
            return new FindTestsForSymbolResult(symbol, [], []);

        var directTests = new List<TestReference>();
        var seenTestSymbols = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var targetSet = new HashSet<IMethodSymbol>(targetMethods, SymbolEqualityComparer.Default);

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            if (!testProjectIds.Contains(projectId))
                continue;

            var projectName = source.GetProjectName(projectId);

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    if (symbolInfo.Symbol is not IMethodSymbol calledMethod)
                        continue;

                    if (!targetSet.Contains(calledMethod) && !targetSet.Contains(calledMethod.OriginalDefinition))
                        continue;

                    // Find the enclosing method symbol
                    var enclosingMethod = FindEnclosingMethodSymbol(invocation, semanticModel);
                    if (enclosingMethod is null)
                        continue;

                    if (!seenTestSymbols.Add(enclosingMethod))
                        continue;

                    var testInfo = ClassifyAsTest(enclosingMethod, projectName);
                    if (testInfo is not null)
                        directTests.Add(testInfo);
                }
            }
        }

        return new FindTestsForSymbolResult(symbol, directTests, []);
    }

    private static IMethodSymbol? FindEnclosingMethodSymbol(SyntaxNode node, SemanticModel semanticModel)
    {
        var methodDecl = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (methodDecl is null)
            return null;
        return semanticModel.GetDeclaredSymbol(methodDecl);
    }

    private static TestReference? ClassifyAsTest(IMethodSymbol method, string projectName)
    {
        foreach (var attr in method.GetAttributes())
        {
            var ns = attr.AttributeClass?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            var name = attr.AttributeClass?.Name ?? string.Empty;

            var framework = TestAttributeRecognizer.Recognize(ns, name);
            if (framework is not null)
            {
                var location = method.Locations.FirstOrDefault(l => l.IsInSource);
                if (location is null)
                    return null;

                var lineSpan = location.GetLineSpan();
                var attributeShortName = name.EndsWith("Attribute", StringComparison.Ordinal)
                    ? name[..^"Attribute".Length]
                    : name;

                return new TestReference(
                    FullyQualifiedName: method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal),
                    Framework: framework.Value,
                    Attribute: attributeShortName,
                    FilePath: lineSpan.Path,
                    Line: lineSpan.StartLinePosition.Line + 1,
                    Project: projectName);
            }
        }

        return null;
    }
}
```

**Step 6: Run tests to verify they pass**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindTestsForSymbolToolTests" -v normal
```

Expected: all 4 tests pass.

**Step 7: Commit**

```bash
git add src/RoslynCodeLens/Models/TestReference.cs src/RoslynCodeLens/Models/FindTestsForSymbolResult.cs src/RoslynCodeLens/Tools/FindTestsForSymbolLogic.cs tests/RoslynCodeLens.Tests/Tools/FindTestsForSymbolToolTests.cs
git commit -m "feat: add FindTestsForSymbolLogic (direct mode)"
```

---

## Task 5: Add transitive mode to FindTestsForSymbolLogic

BFS outward from the target. For each frontier method, find its callers. Test methods become terminal results in `TransitiveTests`. Production callers are enqueued for further walking, bounded by `maxDepth`.

**Files:**
- Modify: `src/RoslynCodeLens/Tools/FindTestsForSymbolLogic.cs`
- Modify: `tests/RoslynCodeLens.Tests/Tools/FindTestsForSymbolToolTests.cs`

**Step 1: Add failing transitive tests to the existing test class**

Append these inside `FindTestsForSymbolToolTests`:

```csharp
[Fact]
public void Transitive_FindsTestViaHelper()
{
    var result = FindTestsForSymbolLogic.Execute(
        _loaded, _resolver, _metadata, "Greeter.Greet", transitive: true, maxDepth: 3);

    // TransitiveGreetTest reaches Greet only through HelperThatGreets.
    var transitive = result.TransitiveTests.SingleOrDefault(t =>
        t.FullyQualifiedName.EndsWith("TransitiveGreetTest", StringComparison.Ordinal));

    Assert.NotNull(transitive);
    Assert.NotNull(transitive!.CallChain);
    Assert.Contains("HelperThatGreets", transitive.CallChain!);
    Assert.Equal("Greet", transitive.CallChain![^1]);  // target last
}

[Fact]
public void Transitive_DirectHitsStillInDirectBucket()
{
    var result = FindTestsForSymbolLogic.Execute(
        _loaded, _resolver, _metadata, "Greeter.Greet", transitive: true, maxDepth: 3);

    // DirectGreetTest is a direct caller; it must remain in DirectTests, not TransitiveTests.
    Assert.Contains(result.DirectTests, t =>
        t.FullyQualifiedName.EndsWith("DirectGreetTest", StringComparison.Ordinal));
    Assert.DoesNotContain(result.TransitiveTests, t =>
        t.FullyQualifiedName.EndsWith("DirectGreetTest", StringComparison.Ordinal));
}

[Fact]
public void Transitive_RespectsMaxDepth()
{
    // maxDepth=1 means the walk only inspects direct callers. The transitive helper
    // (depth-2 from the target) must not produce a transitive hit.
    var result = FindTestsForSymbolLogic.Execute(
        _loaded, _resolver, _metadata, "Greeter.Greet", transitive: true, maxDepth: 1);

    Assert.DoesNotContain(result.TransitiveTests, t =>
        t.FullyQualifiedName.EndsWith("TransitiveGreetTest", StringComparison.Ordinal));
}
```

**Step 2: Run to verify they fail**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindTestsForSymbolToolTests" -v normal
```

Expected: 3 new tests fail (existing direct-mode tests still pass).

**Step 3: Modify `FindTestsForSymbolLogic.Execute` to support transitive**

Replace the entire `FindTestsForSymbolLogic.cs` with:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class FindTestsForSymbolLogic
{
    public static FindTestsForSymbolResult Execute(
        LoadedSolution loaded,
        SymbolResolver source,
        MetadataSymbolResolver metadata,
        string symbol,
        bool transitive = false,
        int maxDepth = 3)
    {
        maxDepth = Math.Clamp(maxDepth, 1, 5);

        var targetMethods = source.FindMethods(symbol);
        if (targetMethods.Count == 0)
            return new FindTestsForSymbolResult(symbol, [], []);

        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);
        if (testProjectIds.IsEmpty)
            return new FindTestsForSymbolResult(symbol, [], []);

        var directTests = new List<TestReference>();
        var transitiveTests = new List<TestReference>();
        var seenTestSymbols = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        // BFS: each entry is (method we want callers for, chain so far ending with the target method's short name)
        var queue = new Queue<(IMethodSymbol Method, List<string> Chain, int Depth)>();
        foreach (var t in targetMethods)
        {
            queue.Enqueue((t, [t.Name], 0));
            visited.Add(t);
        }

        while (queue.Count > 0)
        {
            var (frontier, chain, depth) = queue.Dequeue();

            foreach (var caller in EnumerateDirectCallers(loaded, source, testProjectIds, frontier))
            {
                if (!visited.Add(caller.Method))
                    continue;

                var classification = ClassifyAsTest(caller.Method, caller.ProjectName);
                if (classification is not null)
                {
                    if (!seenTestSymbols.Add(caller.Method))
                        continue;

                    if (depth == 0)
                    {
                        // Direct hit
                        directTests.Add(classification);
                    }
                    else if (transitive)
                    {
                        // Transitive hit — attach call chain (caller's path through helpers to target)
                        transitiveTests.Add(classification with { CallChain = chain });
                    }
                    // Tests are terminal — never expand past them.
                    continue;
                }

                // Non-test caller. In transitive mode, enqueue if depth budget remains.
                if (transitive && depth + 1 < maxDepth)
                {
                    var newChain = new List<string>(chain.Count + 1) { caller.Method.Name };
                    newChain.AddRange(chain);
                    queue.Enqueue((caller.Method, newChain, depth + 1));
                }
            }
        }

        return new FindTestsForSymbolResult(symbol, directTests, transitiveTests);
    }

    private record DirectCaller(IMethodSymbol Method, string ProjectName);

    private static IEnumerable<DirectCaller> EnumerateDirectCallers(
        LoadedSolution loaded,
        SymbolResolver source,
        IReadOnlySet<ProjectId> testProjectIds,
        IMethodSymbol target)
    {
        // Two-pass scan: test projects (terminal lookups) and non-test projects
        // (helper hops in transitive mode). We need both because a helper might live
        // in TestLib2 (non-test project) and a test in NUnitFixture invokes it.
        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var projectName = source.GetProjectName(projectId);

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    if (symbolInfo.Symbol is not IMethodSymbol calledMethod)
                        continue;

                    if (!SymbolEqualityComparer.Default.Equals(calledMethod, target) &&
                        !SymbolEqualityComparer.Default.Equals(calledMethod.OriginalDefinition, target) &&
                        !SymbolEqualityComparer.Default.Equals(calledMethod, target.OriginalDefinition))
                        continue;

                    var enclosing = FindEnclosingMethodSymbol(invocation, semanticModel);
                    if (enclosing is null)
                        continue;

                    yield return new DirectCaller(enclosing, projectName);
                }
            }
        }
    }

    private static IMethodSymbol? FindEnclosingMethodSymbol(SyntaxNode node, SemanticModel semanticModel)
    {
        var methodDecl = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (methodDecl is null)
            return null;
        return semanticModel.GetDeclaredSymbol(methodDecl);
    }

    private static TestReference? ClassifyAsTest(IMethodSymbol method, string projectName)
    {
        foreach (var attr in method.GetAttributes())
        {
            var ns = attr.AttributeClass?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            var name = attr.AttributeClass?.Name ?? string.Empty;

            var framework = TestAttributeRecognizer.Recognize(ns, name);
            if (framework is not null)
            {
                var location = method.Locations.FirstOrDefault(l => l.IsInSource);
                if (location is null)
                    return null;

                var lineSpan = location.GetLineSpan();
                var attributeShortName = name.EndsWith("Attribute", StringComparison.Ordinal)
                    ? name[..^"Attribute".Length]
                    : name;

                return new TestReference(
                    FullyQualifiedName: method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal),
                    Framework: framework.Value,
                    Attribute: attributeShortName,
                    FilePath: lineSpan.Path,
                    Line: lineSpan.StartLinePosition.Line + 1,
                    Project: projectName);
            }
        }

        return null;
    }
}
```

**Step 4: Run all FindTestsForSymbol tests**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindTestsForSymbolToolTests" -v normal
```

Expected: all 7 tests pass (4 from Task 4 + 3 new transitive ones).

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/FindTestsForSymbolLogic.cs tests/RoslynCodeLens.Tests/Tools/FindTestsForSymbolToolTests.cs
git commit -m "feat: add transitive mode to FindTestsForSymbolLogic"
```

---

## Task 6: FindTestsForSymbolTool (MCP wrapper) + registration

The thin MCP-attribute wrapper, plus registering it on the server.

**Files:**
- Create: `src/RoslynCodeLens/Tools/FindTestsForSymbolTool.cs`
- Modify: `src/RoslynCodeLens/Program.cs` (add the new tool to the registration list — read this file first to see how existing tools are registered)

**Step 1: Create `FindTestsForSymbolTool.cs`**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindTestsForSymbolTool
{
    [McpServerTool(Name = "find_tests_for_symbol"),
     Description("List test methods that exercise the given production symbol. Recognises xUnit, NUnit, and MSTest. Set transitive=true to follow helper methods up to maxDepth levels (default 3, max 5).")]
    public static FindTestsForSymbolResult Execute(
        MultiSolutionManager manager,
        [Description("Symbol name as Type.Method (simple or fully qualified)")] string symbol,
        [Description("Walk through helper methods to find indirect tests. Default false.")] bool transitive = false,
        [Description("Maximum walk depth when transitive=true. Clamped to [1, 5]. Default 3.")] int maxDepth = 3)
    {
        manager.EnsureLoaded();
        return FindTestsForSymbolLogic.Execute(
            manager.GetLoadedSolution(),
            manager.GetResolver(),
            manager.GetMetadataResolver(),
            symbol,
            transitive,
            maxDepth);
    }
}
```

**Step 2: Read `src/RoslynCodeLens/Program.cs`** to find where tools are registered. The file uses `WithTools<...>()` calls or similar via the MCP server builder. Add `FindTestsForSymbolTool` to that list, in alphabetical order if there's a convention.

**Step 3: Build the whole solution**

```bash
dotnet build
```

Expected: 0 errors.

**Step 4: Run the full test suite**

```bash
dotnet test
```

Expected: all tests pass (existing + the 7 new ones from Tasks 4–5 + the framework recognizer + the project detector tests).

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/FindTestsForSymbolTool.cs src/RoslynCodeLens/Program.cs
git commit -m "feat: register find_tests_for_symbol MCP tool"
```

---

## Task 7: Add benchmark

**Files:**
- Modify: `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

**Step 1: Read the file** to understand the existing benchmark pattern (each `[Benchmark(Description = "...")]` method, fields like `_loaded`, `_resolver`, `_metadata`).

**Step 2: Add a new benchmark method** alongside the existing ones, near the other `find_*` benchmarks:

```csharp
[Benchmark(Description = "find_tests_for_symbol: IGreeter.Greet")]
public object FindTestsForSymbol()
{
    return FindTestsForSymbolLogic.Execute(
        _loaded, _resolver, _metadata, "IGreeter.Greet", transitive: false, maxDepth: 3);
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
git commit -m "bench: add find_tests_for_symbol benchmark"
```

---

## Task 8: Update SKILL.md and README

Make agents aware the tool exists. The SKILL.md routes natural-language questions to MCP tools; the README lists tools and shows perf numbers.

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
- Modify: `README.md`

**Step 1: Read `SKILL.md`** to see the routing-table format.

**Step 2: Add a row to the routing table** (place it near other navigation tools like `find_callers`):

> | "What tests cover this method?" / "Which tests will break if I change X?" | `find_tests_for_symbol` |

If there's a section listing all 36 tools, increment the count and add `find_tests_for_symbol` with a one-line description.

**Step 3: Read `README.md`** — it has a Features list (with each tool described in one line) and a Performance table.

**Step 4: Add `find_tests_for_symbol` to the Features list** near `find_callers`:

> - **find_tests_for_symbol** — List xUnit/NUnit/MSTest methods that exercise a production symbol; opt-in transitive walk through helpers

(Update the tool count in any "36 tools" prose to "37".)

**Step 5: Skip the perf-table update for now** — that requires running the benchmark suite, which can be done in a follow-up commit when timing matters.

**Step 6: Run the full test suite once more, sanity check**

```bash
dotnet test
```

Expected: all green.

**Step 7: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md README.md
git commit -m "docs: announce find_tests_for_symbol in SKILL.md and README"
```

---

## Done

After Task 8 the branch should have ~8 commits, all tests green, the benchmark project compiling. From there: open a PR for review.
