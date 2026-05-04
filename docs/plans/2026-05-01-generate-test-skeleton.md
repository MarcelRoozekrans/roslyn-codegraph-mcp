# `generate_test_skeleton` Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add MCP tool `generate_test_skeleton` that emits a compilable test-class skeleton (as text) for a method or type. Returns framework, suggested file path, class name, full file contents, and TodoNotes for things the agent has to wire up (e.g. constructor dependencies). The agent decides whether to `Write` the file.

**Architecture:** Resolve the input symbol via `SymbolResolver.FindSymbols` to either `INamedTypeSymbol` (full test class) or `IMethodSymbol` (single test stub wrapped in class). Auto-detect the dominant test framework across the solution (xUnit/NUnit/MSTest), with explicit override. Walk method bodies via `SemanticModel` to find direct `throw new T(...)` exception types. Emit C# source via plain string composition (not `SyntaxFactory`) — stubs are fixed-shape and string-built code is more readable. Suggested file path mirrors source folder structure into a test project that references the production project.

**Tech Stack:** Roslyn (`Microsoft.CodeAnalysis`, `Microsoft.CodeAnalysis.CSharp.Syntax`), xUnit, BenchmarkDotNet, ModelContextProtocol.Server.

**Reference design:** `docs/plans/2026-05-01-generate-test-skeleton-design.md`

**Patterns to mirror:**
- Symbol entry point: `RoslynCodeLens.SymbolResolver.FindSymbols(string)` returns `IReadOnlyList<ISymbol>` (either types or members). Use first result.
- Test-project filter: `RoslynCodeLens.TestDiscovery.TestProjectDetector.GetTestProjectIds(loaded.Solution)` (returns `ImmutableHashSet<ProjectId>`).
- Test-framework enum: `RoslynCodeLens.TestDiscovery.TestFramework` (`XUnit` / `NUnit` / `MSTest`).
- MCP wrapper auto-registration: `Program.cs` uses `WithToolsFromAssembly()` — no edit needed.
- Tool-shape reference: `src/RoslynCodeLens/Tools/GetTestSummaryTool.cs`.
- Logic-shape reference: `src/RoslynCodeLens/Tools/GetTestSummaryLogic.cs`.
- Test-shape reference: `tests/RoslynCodeLens.Tests/Tools/GetTestSummaryToolTests.cs` (uses `[Collection("TestSolution")]` + shared `TestSolutionFixture`).

---

## Task 1: Model

**Files:**
- Create: `src/RoslynCodeLens/Models/GenerateTestSkeletonResult.cs`

**Step 1: Create the record**

```csharp
namespace RoslynCodeLens.Models;

public record GenerateTestSkeletonResult(
    string Framework,
    string SuggestedFilePath,
    string ClassName,
    string Code,
    IReadOnlyList<string> TodoNotes);
```

**Step 2: Build**

```bash
dotnet build src/RoslynCodeLens
```

Expected: 0 errors.

**Step 3: Commit**

```bash
git add src/RoslynCodeLens/Models/GenerateTestSkeletonResult.cs
git commit -m "feat: add model for generate_test_skeleton"
```

---

## Task 2: `TestFrameworkDetector` helper

Add a small helper to determine which framework a test project uses (xUnit / NUnit / MSTest). `TestProjectDetector` already tells us a project IS a test project — this answers WHICH framework.

**Files:**
- Create: `src/RoslynCodeLens/TestDiscovery/TestFrameworkDetector.cs`
- Create: `tests/RoslynCodeLens.Tests/TestDiscovery/TestFrameworkDetectorTests.cs`

**Step 1: Write failing tests**

`tests/RoslynCodeLens.Tests/TestDiscovery/TestFrameworkDetectorTests.cs`:

```csharp
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tests.TestDiscovery;

[Collection("TestSolution")]
public class TestFrameworkDetectorTests
{
    private readonly TestSolutionFixture _fixture;

    public TestFrameworkDetectorTests(TestSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void DetectFramework_XUnitProject_ReturnsXUnit()
    {
        var project = _fixture.Loaded.Solution.Projects
            .Single(p => p.Name == "XUnitFixture");

        var framework = TestFrameworkDetector.DetectFramework(project);

        Assert.Equal(TestFramework.XUnit, framework);
    }

    [Fact]
    public void DetectFramework_NUnitProject_ReturnsNUnit()
    {
        var project = _fixture.Loaded.Solution.Projects
            .Single(p => p.Name == "NUnitFixture");

        var framework = TestFrameworkDetector.DetectFramework(project);

        Assert.Equal(TestFramework.NUnit, framework);
    }

    [Fact]
    public void DetectFramework_MSTestProject_ReturnsMSTest()
    {
        var project = _fixture.Loaded.Solution.Projects
            .Single(p => p.Name == "MSTestFixture");

        var framework = TestFrameworkDetector.DetectFramework(project);

        Assert.Equal(TestFramework.MSTest, framework);
    }

    [Fact]
    public void DetectFramework_ProductionProject_ReturnsNull()
    {
        var project = _fixture.Loaded.Solution.Projects
            .Single(p => p.Name == "TestLib");

        var framework = TestFrameworkDetector.DetectFramework(project);

        Assert.Null(framework);
    }
}
```

**Step 2: Run tests, verify they fail**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~TestFrameworkDetector" --no-build
```

Expected: compile errors (`TestFrameworkDetector` does not exist).

**Step 3: Implement `TestFrameworkDetector`**

`src/RoslynCodeLens/TestDiscovery/TestFrameworkDetector.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.TestDiscovery;

public static class TestFrameworkDetector
{
    public static TestFramework? DetectFramework(Project project)
    {
        if (project.FilePath is null || !File.Exists(project.FilePath))
            return null;

        var content = File.ReadAllText(project.FilePath);

        // Order matters: xUnit projects can transitively reference NUnit packages
        // in unusual setups, but the explicit `xunit` package pin is authoritative.
        if (content.Contains("PackageReference Include=\"xunit", StringComparison.OrdinalIgnoreCase))
            return TestFramework.XUnit;
        if (content.Contains("PackageReference Include=\"NUnit", StringComparison.OrdinalIgnoreCase))
            return TestFramework.NUnit;
        if (content.Contains("PackageReference Include=\"MSTest", StringComparison.OrdinalIgnoreCase))
            return TestFramework.MSTest;

        return null;
    }
}
```

**Step 4: Run tests, verify pass**

```bash
dotnet build && dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~TestFrameworkDetector" --no-build
```

Expected: 4 passed.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/TestDiscovery/TestFrameworkDetector.cs \
  tests/RoslynCodeLens.Tests/TestDiscovery/TestFrameworkDetectorTests.cs
git commit -m "feat: add TestFrameworkDetector to identify per-project framework"
```

---

## Task 3: `GenerateTestSkeletonLogic` — single-method case (TDD)

Build the Logic class incrementally. Start with the simplest case: input is a single non-async, no-param, void method on an instantiable type → produce a class with one `[Fact]` stub.

**Files:**
- Create: `src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs`

**Step 1: Write failing test for the simplest case**

`tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs`:

```csharp
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GenerateTestSkeletonToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GenerateTestSkeletonToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void Method_GeneratesFactSkeletonForVoidMethod()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.Greeter.Greet",
            framework: "xunit");

        Assert.Equal("XUnit", result.Framework);
        Assert.Equal("GreeterTests", result.ClassName);
        Assert.Contains("[Fact]", result.Code);
        Assert.Contains("public void Greet_HappyPath()", result.Code);
        Assert.Contains("var sut = new Greeter", result.Code);
        Assert.Contains("using Xunit;", result.Code);
        Assert.Contains("namespace TestLib.Tests", result.Code);
    }
}
```

**Step 2: Run, verify fails**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~GenerateTestSkeleton" --no-build
```

Expected: compile error (`GenerateTestSkeletonLogic` does not exist).

**Step 3: Implement minimal Logic**

`src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs`:

```csharp
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class GenerateTestSkeletonLogic
{
    public static GenerateTestSkeletonResult Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string symbol,
        string? framework = null)
    {
        var symbols = resolver.FindSymbols(symbol);
        if (symbols.Count == 0)
            throw new InvalidOperationException($"Symbol not found: {symbol}");

        var first = symbols[0];
        var (targetType, targetMethods) = first switch
        {
            INamedTypeSymbol type => (type, EnumerateEligibleMethods(type).ToList()),
            IMethodSymbol method => (method.ContainingType, new List<IMethodSymbol> { method }),
            _ => throw new InvalidOperationException(
                $"Symbol must be a type or method, got {first.Kind}: {symbol}"),
        };

        var fw = ResolveFramework(loaded, framework);
        var todoNotes = new List<string>();

        var className = $"{targetType.Name}Tests";
        var ns = $"{targetType.ContainingNamespace.ToDisplayString()}.Tests";

        var code = BuildClass(targetType, targetMethods, className, ns, fw, todoNotes);

        var suggestedPath = SuggestFilePath(loaded, targetType, todoNotes);

        return new GenerateTestSkeletonResult(
            Framework: fw.ToString(),
            SuggestedFilePath: suggestedPath,
            ClassName: className,
            Code: code,
            TodoNotes: todoNotes);
    }

    private static IEnumerable<IMethodSymbol> EnumerateEligibleMethods(INamedTypeSymbol type)
    {
        foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (m.MethodKind != MethodKind.Ordinary) continue;
            if (m.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected))
                continue;
            yield return m;
        }
    }

    private static TestFramework ResolveFramework(LoadedSolution loaded, string? overrideName)
    {
        if (overrideName is not null)
        {
            return overrideName.ToLowerInvariant() switch
            {
                "xunit" => TestFramework.XUnit,
                "nunit" => TestFramework.NUnit,
                "mstest" => TestFramework.MSTest,
                _ => throw new InvalidOperationException(
                    $"Unknown framework override '{overrideName}'. Use xunit, nunit, or mstest."),
            };
        }

        var counts = new Dictionary<TestFramework, int>();
        foreach (var p in loaded.Solution.Projects)
        {
            var detected = TestFrameworkDetector.DetectFramework(p);
            if (detected is null) continue;
            counts.TryGetValue(detected.Value, out var n);
            counts[detected.Value] = n + 1;
        }

        if (counts.Count == 0) return TestFramework.XUnit;

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key) // tie → XUnit (enum order: XUnit < NUnit < MSTest)
            .First().Key;
    }

    private static string BuildClass(
        INamedTypeSymbol targetType,
        IReadOnlyList<IMethodSymbol> methods,
        string className,
        string ns,
        TestFramework fw,
        List<string> todoNotes)
    {
        var sb = new StringBuilder();

        // Usings
        var prodNs = targetType.ContainingNamespace.ToDisplayString();
        sb.AppendLine($"using {prodNs};");
        sb.AppendLine(FrameworkUsing(fw));
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"public class {className}");
        sb.AppendLine("{");

        if (methods.Count == 0)
        {
            sb.AppendLine("    // TODO: no public methods detected on " + targetType.Name);
            todoNotes.Add($"{targetType.Name} has no eligible public methods.");
        }

        for (int i = 0; i < methods.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            EmitMethodStub(sb, targetType, methods[i], fw, todoNotes);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string FrameworkUsing(TestFramework fw) => fw switch
    {
        TestFramework.XUnit => "using Xunit;",
        TestFramework.NUnit => "using NUnit.Framework;",
        TestFramework.MSTest => "using Microsoft.VisualStudio.TestTools.UnitTesting;",
        _ => "",
    };

    private static void EmitMethodStub(
        StringBuilder sb,
        INamedTypeSymbol targetType,
        IMethodSymbol method,
        TestFramework fw,
        List<string> todoNotes)
    {
        var factAttr = fw switch
        {
            TestFramework.XUnit => "[Fact]",
            TestFramework.NUnit => "[Test]",
            TestFramework.MSTest => "[TestMethod]",
            _ => "[Fact]",
        };

        sb.AppendLine($"    {factAttr}");
        sb.AppendLine($"    public void {method.Name}_HappyPath()");
        sb.AppendLine("    {");

        if (method.IsStatic)
        {
            sb.AppendLine($"        // TODO: arrange inputs");
            sb.AppendLine($"        {targetType.Name}.{method.Name}();");
        }
        else
        {
            sb.AppendLine($"        var sut = new {targetType.Name}();");
            sb.AppendLine($"        sut.{method.Name}();");
        }

        sb.AppendLine("        // TODO: assert");
        sb.AppendLine("    }");
    }

    private static string SuggestFilePath(
        LoadedSolution loaded,
        INamedTypeSymbol targetType,
        List<string> todoNotes)
    {
        var prodProject = loaded.Solution.Projects
            .FirstOrDefault(p => ContainsType(p, targetType));

        if (prodProject is null)
        {
            todoNotes.Add($"Could not locate production project for {targetType.Name}; using placeholder path.");
            return $"tests/{targetType.Name}Tests.cs";
        }

        var prodProjectName = prodProject.Name;
        var testProject = loaded.Solution.Projects
            .Where(p => p.ProjectReferences.Any(r => r.ProjectId == prodProject.Id))
            .Where(p => TestFrameworkDetector.DetectFramework(p) is not null)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        if (testProject is null)
        {
            todoNotes.Add($"No test project references {prodProjectName}; using placeholder path.");
            return $"tests/{prodProjectName}.Tests/{targetType.Name}Tests.cs";
        }

        return $"tests/{testProject.Name}/{targetType.Name}Tests.cs";
    }

    private static bool ContainsType(Project project, INamedTypeSymbol type)
    {
        foreach (var loc in type.Locations)
        {
            if (!loc.IsInSource || loc.SourceTree is null) continue;
            if (project.Documents.Any(d => d.FilePath == loc.SourceTree.FilePath))
                return true;
        }
        return false;
    }
}
```

**Step 4: Build, run test, verify pass**

```bash
dotnet build && dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~GenerateTestSkeleton" --no-build
```

Expected: 1 passed.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs \
  tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs
git commit -m "feat: add GenerateTestSkeletonLogic single-method case"
```

---

## Task 4: Type input → full class with one stub per public method

**Files:**
- Modify: `tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs` (add test)

**Step 1: Add failing test**

Append to `GenerateTestSkeletonToolTests`:

```csharp
[Fact]
public void Type_GeneratesClassWithFactPerPublicMethod()
{
    var result = GenerateTestSkeletonLogic.Execute(
        _loaded, _resolver,
        symbol: "TestLib.Greeter",
        framework: "xunit");

    Assert.Equal("GreeterTests", result.ClassName);
    // Greeter has at least Greet(); should have a corresponding stub
    Assert.Contains("public void Greet_HappyPath()", result.Code);
}
```

**Step 2: Run — should already pass** (Task 3 implementation handles types)

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~GenerateTestSkeleton" --no-build
```

Expected: 2 passed.

**Step 3: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs
git commit -m "test: cover type-level skeleton generation"
```

---

## Task 5: Static method case

**Files:**
- Modify: `tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs`

**Step 1: Identify a static method in `TestLib` (or add one if none exists)**

Run a quick lookup:

```bash
grep -rn "public static" .worktrees/generate-test-skeleton/tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/ | head
```

If a candidate exists, use its FQN below. Otherwise, add one minimal helper:

`tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/StaticHelper.cs`:

```csharp
namespace TestLib;

public static class StaticHelper
{
    public static int Compute() => 42;
}
```

**Step 2: Add failing test**

```csharp
[Fact]
public void StaticMethod_DoesNotInstantiateSut()
{
    var result = GenerateTestSkeletonLogic.Execute(
        _loaded, _resolver,
        symbol: "TestLib.StaticHelper.Compute",
        framework: "xunit");

    Assert.DoesNotContain("var sut = new", result.Code);
    Assert.Contains("StaticHelper.Compute()", result.Code);
}
```

**Step 3: Build, run — should pass** (Task 3 already handles `IsStatic`)

```bash
dotnet build && dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~GenerateTestSkeleton" --no-build
```

Expected: 3 passed.

**Step 4: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/StaticHelper.cs \
  tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs
git commit -m "feat: cover static-method skeleton generation"
```

(If no fixture file was added, skip the first path in `git add`.)

---

## Task 6: Async method (returns `Task` / `Task<T>`)

**Files:**
- Modify: `src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs`
- Modify: `tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs`
- Possibly: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/AsyncWorker.cs`

**Step 1: Add a fixture method that returns `Task`**

Either find an existing `Task`-returning method in `TestLib` or add one:

`tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/AsyncWorker.cs`:

```csharp
namespace TestLib;

public class AsyncWorker
{
    public Task DoAsync() => Task.CompletedTask;

    public Task<int> ComputeAsync() => Task.FromResult(7);
}
```

**Step 2: Write failing test**

```csharp
[Fact]
public void MethodReturningTask_GeneratesAsyncTest()
{
    var result = GenerateTestSkeletonLogic.Execute(
        _loaded, _resolver,
        symbol: "TestLib.AsyncWorker.DoAsync",
        framework: "xunit");

    Assert.Contains("public async Task DoAsync_HappyPath()", result.Code);
    Assert.Contains("await sut.DoAsync()", result.Code);
    Assert.Contains("using System.Threading.Tasks;", result.Code);
}
```

**Step 3: Run — should fail** (current emitter is sync-only)

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "MethodReturningTask_GeneratesAsyncTest" --no-build
```

Expected: FAIL — `public void DoAsync_HappyPath()` instead of async; no `await`.

**Step 4: Implement async detection**

In `GenerateTestSkeletonLogic.cs`, update `EmitMethodStub` and `BuildClass`:

```csharp
private static bool ReturnsTask(IMethodSymbol method)
{
    var rt = method.ReturnType;
    if (rt is null) return false;
    var name = rt.Name;
    if (name == "Task" || name == "ValueTask")
        return rt.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks";
    return false;
}
```

In `BuildClass`, before the `using Xunit;` line, conditionally append `using System.Threading.Tasks;` if any method returns Task. Refactor like this — replace existing usings block:

```csharp
var prodNs = targetType.ContainingNamespace.ToDisplayString();
var hasAsync = methods.Any(ReturnsTask);

sb.AppendLine($"using {prodNs};");
if (hasAsync) sb.AppendLine("using System.Threading.Tasks;");
sb.AppendLine(FrameworkUsing(fw));
sb.AppendLine();
```

In `EmitMethodStub`, replace the body-emit branch:

```csharp
var isAsync = ReturnsTask(method);
var returnType = isAsync ? "async Task" : "void";
var asyncKw = isAsync ? "await " : "";
var awaitable = isAsync ? "await " : "";

sb.AppendLine($"    {factAttr}");
sb.AppendLine($"    public {returnType} {method.Name}_HappyPath()");
sb.AppendLine("    {");

if (method.IsStatic)
{
    sb.AppendLine($"        // TODO: arrange inputs");
    sb.AppendLine($"        {awaitable}{targetType.Name}.{method.Name}();");
}
else
{
    sb.AppendLine($"        var sut = new {targetType.Name}();");
    sb.AppendLine($"        {awaitable}sut.{method.Name}();");
}

sb.AppendLine("        // TODO: assert");
sb.AppendLine("    }");
```

(Remove the unused `asyncKw` variable — keep just `awaitable`.)

**Step 5: Build, run — verify pass**

```bash
dotnet build && dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~GenerateTestSkeleton" --no-build
```

Expected: 4 passed.

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs \
  tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs \
  tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/AsyncWorker.cs
git commit -m "feat: emit async stub for Task-returning methods"
```

---

## Task 7: Theory-with-InlineData for primitive-param methods

**Files:**
- Modify: `src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs`
- Modify: `tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs`

**Step 1: Add fixture method (or use `Greet(string)`)**

`Greeter.Greet(string name)` already exists per prior tools. Otherwise add:

`tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/Calculator.cs`:

```csharp
namespace TestLib;

public class Calculator
{
    public int Add(int a, int b) => a + b;
}
```

**Step 2: Write failing test**

```csharp
[Fact]
public void MethodWithPrimitiveParams_GeneratesTheoryWithInlineData()
{
    var result = GenerateTestSkeletonLogic.Execute(
        _loaded, _resolver,
        symbol: "TestLib.Calculator.Add",
        framework: "xunit");

    Assert.Contains("[Theory]", result.Code);
    Assert.Contains("[InlineData(", result.Code);
    Assert.Contains("public void Add_Theory(int a, int b)", result.Code);
}
```

**Step 3: Run — verify fails**

Expected: FAIL — current emitter only generates `[Fact]`.

**Step 4: Implement primitive-param detection + Theory emission**

Add helpers to `GenerateTestSkeletonLogic.cs`:

```csharp
private static bool IsPrimitiveParam(IParameterSymbol p)
{
    var t = p.Type;
    if (t.SpecialType is
        SpecialType.System_String or
        SpecialType.System_Int32 or SpecialType.System_Int64 or
        SpecialType.System_Double or SpecialType.System_Single or
        SpecialType.System_Boolean or SpecialType.System_Char or
        SpecialType.System_Byte) return true;
    return t.TypeKind == TypeKind.Enum;
}

private static string DefaultLiteral(IParameterSymbol p) => p.Type.SpecialType switch
{
    SpecialType.System_String => "\"\"",
    SpecialType.System_Boolean => "false",
    SpecialType.System_Char => "'a'",
    _ => "0",
};
```

Modify `EmitMethodStub`. Right before the existing happy-path emit, add a branch for parametric methods (only when not async and has params and all primitives):

```csharp
var hasParams = method.Parameters.Length > 0;
var allPrimitive = hasParams && method.Parameters.All(IsPrimitiveParam);

if (allPrimitive && !isAsync)
{
    EmitTheoryStub(sb, targetType, method, fw);
    return;
}
```

Add:

```csharp
private static void EmitTheoryStub(
    StringBuilder sb,
    INamedTypeSymbol targetType,
    IMethodSymbol method,
    TestFramework fw)
{
    var theoryAttr = fw switch
    {
        TestFramework.XUnit => "[Theory]",
        TestFramework.NUnit => "[TestCase(" + string.Join(", ", method.Parameters.Select(DefaultLiteral)) + ")]",
        TestFramework.MSTest => "[DataTestMethod]",
        _ => "[Theory]",
    };

    if (fw == TestFramework.XUnit)
    {
        sb.AppendLine("    [Theory]");
        sb.AppendLine($"    [InlineData({string.Join(", ", method.Parameters.Select(DefaultLiteral))})]");
    }
    else if (fw == TestFramework.NUnit)
    {
        sb.AppendLine($"    {theoryAttr}");
    }
    else
    {
        sb.AppendLine("    [DataTestMethod]");
        sb.AppendLine($"    [DataRow({string.Join(", ", method.Parameters.Select(DefaultLiteral))})]");
    }

    var paramList = string.Join(", ", method.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"));
    var argList = string.Join(", ", method.Parameters.Select(p => p.Name));

    sb.AppendLine($"    public void {method.Name}_Theory({paramList})");
    sb.AppendLine("    {");
    if (method.IsStatic)
        sb.AppendLine($"        {targetType.Name}.{method.Name}({argList});");
    else
    {
        sb.AppendLine($"        var sut = new {targetType.Name}();");
        sb.AppendLine($"        sut.{method.Name}({argList});");
    }
    sb.AppendLine("        // TODO: assert");
    sb.AppendLine("    }");
}
```

**Step 5: Build, run — verify pass**

```bash
dotnet build && dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~GenerateTestSkeleton" --no-build
```

Expected: 5 passed.

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs \
  tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs \
  tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/Calculator.cs
git commit -m "feat: emit Theory + InlineData for primitive-param methods"
```

---

## Task 8: Throw → `Assert.Throws<T>` stub per distinct exception type

**Files:**
- Modify: `src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs`
- Modify: `tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs`

**Step 1: Add fixture method that throws**

`tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/Validator.cs`:

```csharp
namespace TestLib;

public class Validator
{
    public void Validate(string input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (input.Length == 0) throw new ArgumentException("Empty");
    }
}
```

**Step 2: Write failing test**

```csharp
[Fact]
public void MethodThrowingException_GeneratesAssertThrowsStub()
{
    var result = GenerateTestSkeletonLogic.Execute(
        _loaded, _resolver,
        symbol: "TestLib.Validator.Validate",
        framework: "xunit");

    Assert.Contains("Validate_ThrowsArgumentNullException", result.Code);
    Assert.Contains("Validate_ThrowsArgumentException", result.Code);
    Assert.Contains("Assert.Throws<ArgumentNullException>", result.Code);
    Assert.Contains("Assert.Throws<ArgumentException>", result.Code);
}
```

**Step 3: Run — verify fails**

**Step 4: Implement throw-walk + Assert.Throws emission**

Add to `GenerateTestSkeletonLogic.cs`:

```csharp
private static IReadOnlyList<string> CollectThrownExceptionTypes(IMethodSymbol method, Compilation compilation)
{
    var location = method.Locations.FirstOrDefault(l => l.IsInSource);
    if (location?.SourceTree is null) return [];

    var node = location.SourceTree.GetRoot().FindNode(location.SourceSpan);
    if (node is null) return [];

    var sm = compilation.GetSemanticModel(location.SourceTree);
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var ordered = new List<string>();

    foreach (var n in node.DescendantNodes())
    {
        ObjectCreationExpressionSyntax? ctor = n switch
        {
            ThrowStatementSyntax t => t.Expression as ObjectCreationExpressionSyntax,
            ThrowExpressionSyntax t => t.Expression as ObjectCreationExpressionSyntax,
            _ => null,
        };
        if (ctor is null) continue;

        var typeSymbol = sm.GetTypeInfo(ctor).Type as INamedTypeSymbol;
        if (typeSymbol is null) continue;

        var name = typeSymbol.Name;
        if (seen.Add(name))
            ordered.Add(name);
    }

    return ordered;
}
```

In `Execute`, locate the right compilation:

```csharp
var compilation = loaded.Compilations.Values
    .First(c => SymbolEqualityComparer.Default.Equals(c.Assembly, targetType.ContainingAssembly));
```

Pass compilation through to `BuildClass` / `EmitMethodStub`.

In `EmitMethodStub`, after the existing happy-path emit, append per-exception stubs:

```csharp
var thrown = CollectThrownExceptionTypes(method, compilation);
foreach (var ex in thrown)
{
    sb.AppendLine();
    sb.AppendLine($"    {factAttr}");
    var asyncReturn = isAsync ? "async Task" : "void";
    sb.AppendLine($"    public {asyncReturn} {method.Name}_Throws{ex}()");
    sb.AppendLine("    {");
    if (method.IsStatic)
    {
        if (isAsync)
            sb.AppendLine($"        await Assert.ThrowsAsync<{ex}>(() => {targetType.Name}.{method.Name}());");
        else
            sb.AppendLine($"        Assert.Throws<{ex}>(() => {targetType.Name}.{method.Name}());");
    }
    else
    {
        sb.AppendLine($"        var sut = new {targetType.Name}();");
        if (isAsync)
            sb.AppendLine($"        await Assert.ThrowsAsync<{ex}>(() => sut.{method.Name}());");
        else
            sb.AppendLine($"        Assert.Throws<{ex}>(() => sut.{method.Name}());");
    }
    sb.AppendLine("    }");
}
```

For `Validator.Validate(string)` — note this method has a primitive param so it's the Theory branch. Adjust: throw-walk should run after either Theory or Fact emission (not "instead of"). Refactor the entry of `EmitMethodStub`:

```csharp
if (allPrimitive && !isAsync)
{
    EmitTheoryStub(sb, targetType, method, fw);
}
else
{
    EmitHappyPathFact(sb, targetType, method, fw, isAsync);
}

EmitThrowStubs(sb, targetType, method, fw, isAsync, compilation);
```

(Extract the existing Fact-emit body into `EmitHappyPathFact`, and the throw-loop into `EmitThrowStubs`.)

**Step 5: Build, run — verify pass**

```bash
dotnet build && dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~GenerateTestSkeleton" --no-build
```

Expected: 6 passed.

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs \
  tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs \
  tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/Validator.cs
git commit -m "feat: emit Assert.Throws stub per distinct exception type"
```

---

## Task 9: Constructor dependencies → TodoNotes

**Files:**
- Modify: `src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs`
- Modify: `tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs`

**Step 1: Add fixture type with non-trivial ctor**

`tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/OrderService.cs`:

```csharp
namespace TestLib;

public interface IOrderRepo { void Save(string id); }

public class OrderService
{
    private readonly IOrderRepo _repo;
    public OrderService(IOrderRepo repo) { _repo = repo; }

    public void Place(string id) => _repo.Save(id);
}
```

**Step 2: Write failing test**

```csharp
[Fact]
public void TodoNotes_IncludeConstructorDependencies()
{
    var result = GenerateTestSkeletonLogic.Execute(
        _loaded, _resolver,
        symbol: "TestLib.OrderService",
        framework: "xunit");

    Assert.Contains(
        result.TodoNotes,
        n => n.Contains("IOrderRepo", StringComparison.Ordinal));
    Assert.Contains("/* TODO: dependencies */", result.Code);
}
```

**Step 3: Run — verify fails**

**Step 4: Implement ctor-aware SUT instantiation**

In `EmitHappyPathFact` and the throw branch, replace `new {Type}()` with a helper:

```csharp
private static string SutCreation(INamedTypeSymbol type, List<string> todoNotes)
{
    if (type.IsStatic) return ""; // never used, all-static path
    var ctor = type.InstanceConstructors
        .Where(c => c.DeclaredAccessibility == Accessibility.Public)
        .OrderBy(c => c.Parameters.Length)
        .FirstOrDefault();

    if (ctor is null || ctor.Parameters.Length == 0)
        return $"new {type.Name}()";

    foreach (var p in ctor.Parameters)
        todoNotes.Add($"{type.Name} constructor needs {p.Type.ToDisplayString()} {p.Name} — wire mock or instance.");

    return $"new {type.Name}(/* TODO: dependencies */)";
}
```

Update call sites in `EmitHappyPathFact` / `EmitThrowStubs`:

```csharp
sb.AppendLine($"        var sut = {SutCreation(targetType, todoNotes)};");
```

(Pass `todoNotes` down through `EmitMethodStub` / friends.)

Also add a note for abstract types:

In `BuildClass`, near the top:

```csharp
if (targetType.IsAbstract)
    todoNotes.Add($"{targetType.Name} is abstract — instantiate via a derived test fixture.");
```

**Step 5: Build, run — verify pass**

```bash
dotnet build && dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~GenerateTestSkeleton" --no-build
```

Expected: 7 passed.

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs \
  tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs \
  tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/OrderService.cs
git commit -m "feat: surface constructor dependencies as TodoNotes"
```

---

## Task 10: Excludes properties, ctors, indexers, operators

**Files:**
- Modify: `tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs`

**Step 1: Add failing tests**

```csharp
[Fact]
public void Type_ExcludesPropertiesAndConstructors()
{
    var result = GenerateTestSkeletonLogic.Execute(
        _loaded, _resolver,
        symbol: "TestLib.OrderService",
        framework: "xunit");

    Assert.DoesNotContain("public void .ctor", result.Code);
    Assert.DoesNotContain("get_", result.Code);
    Assert.DoesNotContain("set_", result.Code);
}
```

**Step 2: Run — should already pass** (Task 3's `EnumerateEligibleMethods` filters `MethodKind.Ordinary`)

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~GenerateTestSkeleton" --no-build
```

Expected: 8 passed.

**Step 3: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs
git commit -m "test: lock in property/ctor/indexer exclusion"
```

---

## Task 11: Framework auto-detect

**Files:**
- Modify: `tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs`

**Step 1: Add failing test**

```csharp
[Fact]
public void Framework_AutoDetectsXUnitFromTestProjects()
{
    // Test solution has 1 each of XUnit / NUnit / MSTest fixtures.
    // Tie → XUnit (enum order: XUnit < NUnit < MSTest).
    var result = GenerateTestSkeletonLogic.Execute(
        _loaded, _resolver,
        symbol: "TestLib.Greeter.Greet",
        framework: null);

    Assert.Equal("XUnit", result.Framework);
}

[Fact]
public void Framework_OverrideHonored()
{
    var result = GenerateTestSkeletonLogic.Execute(
        _loaded, _resolver,
        symbol: "TestLib.Greeter.Greet",
        framework: "nunit");

    Assert.Equal("NUnit", result.Framework);
    Assert.Contains("[Test]", result.Code);
    Assert.Contains("using NUnit.Framework;", result.Code);
}
```

**Step 2: Run, verify pass**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~GenerateTestSkeleton" --no-build
```

Expected: 10 passed.

**Step 3: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs
git commit -m "test: cover framework auto-detect and override"
```

---

## Task 12: Suggested file path

**Files:**
- Modify: `tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs`

**Step 1: Add failing test**

```csharp
[Fact]
public void SuggestedPath_TargetsTestProject()
{
    var result = GenerateTestSkeletonLogic.Execute(
        _loaded, _resolver,
        symbol: "TestLib.Greeter",
        framework: "xunit");

    // Some test project that references TestLib should be the target.
    Assert.Contains("Tests", result.SuggestedFilePath, StringComparison.Ordinal);
    Assert.EndsWith("GreeterTests.cs", result.SuggestedFilePath, StringComparison.Ordinal);
}
```

**Step 2: Run, verify pass** (Task 3's `SuggestFilePath` already covers this)

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~GenerateTestSkeleton" --no-build
```

Expected: 11 passed.

**Step 3: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs
git commit -m "test: lock in suggested-path shape"
```

---

## Task 13: Unknown symbol → error

**Files:**
- Modify: `tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs`

**Step 1: Add failing test**

```csharp
[Fact]
public void UnknownSymbol_Throws()
{
    var ex = Assert.Throws<InvalidOperationException>(() =>
        GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.DoesNotExist",
            framework: "xunit"));

    Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
}
```

**Step 2: Run, verify pass**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~GenerateTestSkeleton" --no-build
```

Expected: 12 passed.

**Step 3: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs
git commit -m "test: lock in unknown-symbol error"
```

---

## Task 14: Generated output is parseable C#

Sanity check: whatever we emit must at least parse cleanly. This catches regressions when stub composition gets out of sync.

**Files:**
- Modify: `tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs`

**Step 1: Add failing test**

```csharp
[Fact]
public void Code_IsSyntacticallyValidCSharp()
{
    var result = GenerateTestSkeletonLogic.Execute(
        _loaded, _resolver,
        symbol: "TestLib.OrderService",
        framework: "xunit");

    var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(result.Code);
    var diagnostics = tree.GetDiagnostics().ToList();

    Assert.Empty(diagnostics);
}
```

**Step 2: Run, verify pass**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~GenerateTestSkeleton" --no-build
```

Expected: 13 passed. If failures, the emitter has a syntax error — fix before continuing.

**Step 3: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs
git commit -m "test: assert generated code parses without diagnostics"
```

---

## Task 15: MCP wrapper

**Files:**
- Create: `src/RoslynCodeLens/Tools/GenerateTestSkeletonTool.cs`

**Step 1: Create the wrapper**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GenerateTestSkeletonTool
{
    [McpServerTool(Name = "generate_test_skeleton")]
    [Description(
        "Emits a compilable test-class skeleton (as text) for a method or type. " +
        "Pass a type FQN like 'MyApp.Services.OrderService' to get a full test class " +
        "with one stub per public method, or a method FQN like " +
        "'MyApp.Services.OrderService.PlaceOrder' to get a single stub. Returns " +
        "framework, suggested file path, class name, full file contents (as text), " +
        "and TodoNotes for things to wire up (e.g. constructor dependencies). " +
        "The tool does NOT write to disk — agent decides what to do with the text. " +
        "Pairs naturally with find_uncovered_symbols / get_test_summary. " +
        "Stubs include happy-path Fact, Theory + InlineData for primitive-param " +
        "methods, and Assert.Throws<T> per distinct direct-throw exception type. " +
        "Async (Task / Task<T>) detected automatically. Properties, indexers, " +
        "operators, and constructors are excluded from per-method enumeration. " +
        "Framework auto-detected from solution test projects (tie → xUnit); " +
        "override with framework='xunit' / 'nunit' / 'mstest'.")]
    public static GenerateTestSkeletonResult Execute(
        MultiSolutionManager manager,
        [Description("FQN of a type or method to generate a test skeleton for.")]
        string symbol,
        [Description("Optional framework override: 'xunit', 'nunit', or 'mstest'. Auto-detected if null.")]
        string? framework = null)
    {
        manager.EnsureLoaded();
        return GenerateTestSkeletonLogic.Execute(
            manager.GetLoadedSolution(),
            manager.GetResolver(),
            symbol,
            framework);
    }
}
```

**Step 2: Build**

```bash
dotnet build
```

Expected: 0 errors.

**Step 3: Run all tests to confirm registration is clean**

```bash
dotnet test --no-build
```

Expected: all green.

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Tools/GenerateTestSkeletonTool.cs
git commit -m "feat: register generate_test_skeleton MCP tool"
```

---

## Task 16: Benchmark

**Files:**
- Modify: `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

**Step 1: Locate the benchmark file and existing pattern**

```bash
grep -n "get_test_summary\|GetTestSummary" benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs
```

**Step 2: Add a benchmark next to the existing test-tool benchmarks**

Mirror the get_test_summary pattern but call into `GenerateTestSkeletonLogic.Execute` for `TestLib.OrderService` (type input — exercises full-class path including ctor walk + throw walk if present).

```csharp
[Benchmark]
public GenerateTestSkeletonResult GenerateTestSkeleton_TypeInput()
    => GenerateTestSkeletonLogic.Execute(_loaded, _resolver,
        symbol: "TestLib.OrderService",
        framework: "xunit");
```

**Step 3: Build benchmarks project**

```bash
dotnet build benchmarks/RoslynCodeLens.Benchmarks -c Release
```

Expected: 0 errors.

**Step 4: Commit**

```bash
git add benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs
git commit -m "bench: add generate_test_skeleton benchmark"
```

---

## Task 17: Skill + README + CLAUDE + BACKLOG updates

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
- Modify: `README.md` (Features list)
- Modify: `CLAUDE.md` (bump tool count)
- Modify: `docs/BACKLOG.md`

**Step 1: SKILL.md — add to Quick Reference + Test-aware section**

Find the existing test-aware tools section (where `get_test_summary` / `find_uncovered_symbols` are documented). Add a one-line entry for `generate_test_skeleton` listing the input signature and one example.

**Step 2: README.md Features list**

Insert below the existing test-aware bullets:

```markdown
- **generate_test_skeleton** — Emit a compilable test-class skeleton (as text) for a method or type. Auto-detects xUnit/NUnit/MSTest; surfaces constructor dependencies as TodoNotes; returns suggested file path. Closes the loop with find_uncovered_symbols / get_test_summary.
```

**Step 3: CLAUDE.md — bump tool count**

Locate line: `35 code intelligence tools` (or whatever current count is — `grep -n "code intelligence tools" CLAUDE.md`). Increment by 1.

**Step 4: BACKLOG.md updates**

- Remove `generate_test_skeleton` line from section 5 (Generation & scaffolding).
- Append deferred items under a new "From `generate_test_skeleton` (designed 2026-05-01)" subsection in "Deferred from shipped features":
  - **Property/indexer/operator stubs** — low value; agent can request manually.
  - **Mock framework integration** (Moq, NSubstitute, FakeItEasy) — opinionated; agent picks.
  - **Test data builders** (AutoFixture, Bogus) — same.
  - **Cross-method dependency analysis** — keep skeleton focused on the SUT.
  - **`SyntaxFactory`-based output** — string composition is cleaner for stub-shaped output.
  - **Indirect `throw` detection** (via helper methods) — only direct `throw new T(...)` is followed.
  - **Existing-test detection / merge** — agent handles dedupe.
  - **Inherited-member skeletons** — agent composes via `get_overloads` / hierarchy tools.

**Step 5: Run all tests one more time**

```bash
dotnet test --no-build
```

Expected: all green.

**Step 6: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md \
  README.md CLAUDE.md docs/BACKLOG.md
git commit -m "docs: announce generate_test_skeleton in SKILL/README/CLAUDE/BACKLOG"
```

---

## Task 18: Open PR

**Step 1: Push branch**

```bash
git push -u origin feat/generate-test-skeleton
```

**Step 2: Open PR**

```bash
gh pr create --title "feat: generate_test_skeleton" --body "$(cat <<'EOF'
## Summary

- New MCP tool `generate_test_skeleton` — emits a compilable test-class skeleton (as text) for a method or type.
- Auto-detects xUnit / NUnit / MSTest from the solution's test projects (tie → xUnit); explicit `framework` override.
- Stubs: happy-path `[Fact]`, `[Theory]` + `[InlineData]` for primitive-param methods, `Assert.Throws<T>` per distinct direct-throw exception type. Async detected from `Task` / `Task<T>` return.
- Constructor dependencies surface as TodoNotes — no auto-mocking.
- Tool returns text only; agent uses `Write` to commit to disk.

## Test plan

- [ ] All `GenerateTestSkeleton*` tests pass.
- [ ] Generated code parses without diagnostics (lock-in test).
- [ ] Benchmark runs cleanly.
- [ ] CI green.
EOF
)"
```

---

## Self-test checklist (run after Task 17, before Task 18)

- [ ] `dotnet build` clean.
- [ ] `dotnet test --no-build` all green.
- [ ] `dotnet test --filter "FullyQualifiedName~GenerateTestSkeleton" --no-build` ≥ 13 tests passed.
- [ ] `dotnet test --filter "FullyQualifiedName~TestFrameworkDetector" --no-build` 4 tests passed.
- [ ] Tool count in `CLAUDE.md` matches the actual auto-registered count.
- [ ] `BACKLOG.md` no longer lists `generate_test_skeleton` in section 5.

## Troubleshooting

- **"Symbol not found"** when calling with valid FQN: ensure NuGet packages restored for the test fixture solution. From the worktree root: `dotnet restore tests/RoslynCodeLens.Tests/Fixtures/TestSolution`.
- **Generated code fails the parseability test**: run the test, then print `result.Code` and the diagnostic span. Most common cause is a stale string template missing a brace or semicolon.
- **Throw walk returns empty list for a method that obviously throws**: the throw is via a helper (`ThrowHelper.ArgumentNull(...)`) or via a re-thrown caught exception. Out of scope — direct `throw new T(...)` only.
- **Auto-detect picks the wrong framework on a real solution**: the dominant-framework heuristic counts test projects. If the solution has 5 NUnit and 1 xUnit, NUnit wins. Override explicitly with `framework: "xunit"`.
- **`SuggestedFilePath` falls back to placeholder**: no test project references the production project. The TodoNote will say so.
