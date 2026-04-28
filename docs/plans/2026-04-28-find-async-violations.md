# `find_async_violations` Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a new MCP tool `find_async_violations` that detects six classes of async/await misuse (sync-over-async × 3, async void outside event handlers, missing await, fire-and-forget) across non-test projects, returning a summary plus a flat per-violation list with severity (error/warning).

**Architecture:** Single forward sweep per non-test compilation. For each method declaration: check the method itself for `AsyncVoid`, then walk its body for the other five patterns via `MemberAccessExpressionSyntax` (`.Result`), `InvocationExpressionSyntax` (`Wait*`, `GetAwaiter().GetResult()`), and `ExpressionStatementSyntax` (Task-returning bare expressions → MissingAwait if inside async, FireAndForget otherwise). Reuses `TestProjectDetector` to skip test projects.

**Tech Stack:** Roslyn (`Microsoft.CodeAnalysis`), xUnit, BenchmarkDotNet, ModelContextProtocol.Server.

**Reference design:** `docs/plans/2026-04-28-find-async-violations-design.md`

**Patterns to mirror (read these before starting):**
- Tool wrapper: `src/RoslynCodeLens/Tools/FindUncoveredSymbolsTool.cs` (parameter-less wrapper)
- Logic class: `src/RoslynCodeLens/Tools/FindUncoveredSymbolsLogic.cs` (production-only filter via `TestProjectDetector`, single-pass enumeration)
- Test pattern: `tests/RoslynCodeLens.Tests/Tools/FindUncoveredSymbolsToolTests.cs` (fixture loading via `IAsyncLifetime`)
- MCP auto-registration: `src/RoslynCodeLens/Program.cs:35` uses `WithToolsFromAssembly()` — no `Program.cs` edit needed

---

## Task 1: Add `AsyncFixture` production project

The fixture solution needs a project containing one positive case per pattern + several negative cases. The project must NOT reference any test framework (so it counts as a production project under `TestProjectDetector`).

**Files:**
- Create: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/AsyncFixture/AsyncFixture.csproj`
- Create: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/AsyncFixture/Violations.cs`
- Modify: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestSolution.slnx`

**Step 1: Create `AsyncFixture.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

(No `<ProjectReference>` to TestLib/TestLib2 — this fixture is self-contained.)

**Step 2: Create `Violations.cs`**

```csharp
using System;
using System.Threading.Tasks;

namespace AsyncFixture;

public class Violations
{
    // ============ POSITIVE CASES (each must produce exactly one violation) ============

    // 1. SyncOverAsyncResult: .Result on Task<T>
    public string GetResultViolation()
    {
        var task = Task.FromResult("hello");
        return task.Result;
    }

    // 2. SyncOverAsyncWait: .Wait() on Task
    public void WaitViolation()
    {
        var task = Task.Delay(10);
        task.Wait();
    }

    // 3. SyncOverAsyncGetAwaiterGetResult
    public string GetAwaiterGetResultViolation()
    {
        var task = Task.FromResult("hello");
        return task.GetAwaiter().GetResult();
    }

    // 4. AsyncVoid (NOT event-handler shaped)
    public async void AsyncVoidViolation()
    {
        await Task.Delay(10);
    }

    // 5. MissingAwait: bare Task call inside async method
    public async Task MissingAwaitViolation()
    {
        Task.Delay(10);
        await Task.CompletedTask;
    }

    // 6. FireAndForget: bare Task call in non-async method
    public void FireAndForgetViolation()
    {
        Task.Delay(10);
    }

    // ============ NEGATIVE CASES (must NOT be flagged) ============

    public async Task ProperAwait()
    {
        await Task.Delay(10);
    }

    public async void EventHandler(object sender, EventArgs e)
    {
        await Task.Delay(10);
    }

    public void DiscardFireAndForget()
    {
        _ = Task.Delay(10);
    }

    public void AssignedFireAndForget()
    {
        var t = Task.Delay(10);
        _ = t;
    }

    public Task ForwardingMethod()
    {
        return Task.Delay(10);
    }
}
```

**Step 3: Update `TestSolution.slnx`** — read first to preserve existing entries, then append the new project:

```xml
<Solution>
  <Project Path="TestLib/TestLib.csproj" />
  <Project Path="TestLib2/TestLib2.csproj" />
  <Project Path="NUnitFixture/NUnitFixture.csproj" />
  <Project Path="MSTestFixture/MSTestFixture.csproj" />
  <Project Path="XUnitFixture/XUnitFixture.csproj" />
  <Project Path="AsyncFixture/AsyncFixture.csproj" />
</Solution>
```

**Step 4: Restore + build the fixture solution**

```bash
dotnet restore tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestSolution.slnx
dotnet build tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestSolution.slnx -c Debug
```

Expected: 0 errors. May emit a CS0219 warning on `AssignedFireAndForget`'s `var t` (unused variable) — that's why we added `_ = t;` to silence it. May emit CS1998 on `AsyncVoidViolation` if it doesn't await — it does, so should be fine.

**Step 5: Run existing test suite to verify no regressions**

```bash
dotnet test tests/RoslynCodeLens.Tests
```

Expected: same pass count as before this task (the new project just adds more candidates for existing tools; nothing should newly fail).

**Step 6: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Fixtures/TestSolution/AsyncFixture/ \
  tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestSolution.slnx
git commit -m "test: add AsyncFixture project with async-violation positive and negative cases"
```

---

## Task 2: Models for output

Five files: two enums + three records.

**Files:**
- Create: `src/RoslynCodeLens/Models/AsyncViolationPattern.cs`
- Create: `src/RoslynCodeLens/Models/AsyncViolationSeverity.cs`
- Create: `src/RoslynCodeLens/Models/AsyncViolation.cs`
- Create: `src/RoslynCodeLens/Models/AsyncViolationSummary.cs`
- Create: `src/RoslynCodeLens/Models/FindAsyncViolationsResult.cs`

**Step 1: `AsyncViolationPattern.cs`**

```csharp
namespace RoslynCodeLens.Models;

public enum AsyncViolationPattern
{
    SyncOverAsyncResult,
    SyncOverAsyncWait,
    SyncOverAsyncGetAwaiterGetResult,
    AsyncVoid,
    MissingAwait,
    FireAndForget
}
```

**Step 2: `AsyncViolationSeverity.cs`**

```csharp
namespace RoslynCodeLens.Models;

public enum AsyncViolationSeverity
{
    Error,
    Warning
}
```

(Order matters: `Error` is value 0 so ascending sort puts errors first.)

**Step 3: `AsyncViolation.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record AsyncViolation(
    AsyncViolationPattern Pattern,
    AsyncViolationSeverity Severity,
    string FilePath,
    int Line,
    string ContainingMethod,
    string Project,
    string Snippet);
```

**Step 4: `AsyncViolationSummary.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record AsyncViolationSummary(
    int TotalViolations,
    IReadOnlyDictionary<string, int> ByPattern,
    IReadOnlyDictionary<string, int> BySeverity);
```

(String keys so JSON serializes cleanly. Values are pattern enum names and severity enum names.)

**Step 5: `FindAsyncViolationsResult.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record FindAsyncViolationsResult(
    AsyncViolationSummary Summary,
    IReadOnlyList<AsyncViolation> Violations);
```

**Step 6: Build to verify**

```bash
dotnet build src/RoslynCodeLens
```

Expected: 0 errors.

**Step 7: Commit**

```bash
git add src/RoslynCodeLens/Models/AsyncViolationPattern.cs \
  src/RoslynCodeLens/Models/AsyncViolationSeverity.cs \
  src/RoslynCodeLens/Models/AsyncViolation.cs \
  src/RoslynCodeLens/Models/AsyncViolationSummary.cs \
  src/RoslynCodeLens/Models/FindAsyncViolationsResult.cs
git commit -m "feat: add models for find_async_violations output"
```

---

## Task 3: `FindAsyncViolationsLogic` + comprehensive tests

The core engine. Single pass, six patterns, severity sort, summary aggregation.

**Files:**
- Create: `src/RoslynCodeLens/Tools/FindAsyncViolationsLogic.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/FindAsyncViolationsToolTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/RoslynCodeLens.Tests/Tools/FindAsyncViolationsToolTests.cs
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindAsyncViolationsToolTests : IAsyncLifetime
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

    [Theory]
    [InlineData(AsyncViolationPattern.SyncOverAsyncResult, "GetResultViolation")]
    [InlineData(AsyncViolationPattern.SyncOverAsyncWait, "WaitViolation")]
    [InlineData(AsyncViolationPattern.SyncOverAsyncGetAwaiterGetResult, "GetAwaiterGetResultViolation")]
    [InlineData(AsyncViolationPattern.AsyncVoid, "AsyncVoidViolation")]
    [InlineData(AsyncViolationPattern.MissingAwait, "MissingAwaitViolation")]
    [InlineData(AsyncViolationPattern.FireAndForget, "FireAndForgetViolation")]
    public void DetectsExactlyOneViolationPerPattern(AsyncViolationPattern pattern, string methodName)
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        var hits = result.Violations.Where(v =>
            v.Pattern == pattern &&
            v.ContainingMethod.EndsWith(methodName, StringComparison.Ordinal)).ToList();

        Assert.Single(hits);
    }

    [Fact]
    public void DoesNotFlag_ProperlyAwaited()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.Violations, v =>
            v.ContainingMethod.EndsWith("ProperAwait", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlag_EventHandlerAsyncVoid()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.Violations, v =>
            v.ContainingMethod.EndsWith("EventHandler", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlag_DiscardOrAssignedTask()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.Violations, v =>
            v.ContainingMethod.EndsWith("DiscardFireAndForget", StringComparison.Ordinal) ||
            v.ContainingMethod.EndsWith("AssignedFireAndForget", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlag_ForwardingReturn()
    {
        // Non-async method that does `return SomeAsync();` — perfectly valid.
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.Violations, v =>
            v.ContainingMethod.EndsWith("ForwardingMethod", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlag_TestProjectMembers()
    {
        // Test fixtures may contain async patterns; they must be skipped.
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.Violations, v =>
            v.Project.EndsWith("Fixture", StringComparison.Ordinal) &&
            v.Project != "AsyncFixture");
    }

    [Fact]
    public void Summary_TotalMatchesListLength()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        Assert.Equal(result.Violations.Count, result.Summary.TotalViolations);
    }

    [Fact]
    public void Summary_ByPatternCountsAreCorrect()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        foreach (var (patternName, count) in result.Summary.ByPattern)
        {
            var actual = result.Violations.Count(v => v.Pattern.ToString() == patternName);
            Assert.Equal(actual, count);
        }
    }

    [Fact]
    public void Summary_BySeverityCountsAreCorrect()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        foreach (var (severityName, count) in result.Summary.BySeverity)
        {
            var actual = result.Violations.Count(v => v.Severity.ToString() == severityName);
            Assert.Equal(actual, count);
        }
    }

    [Fact]
    public void Violations_SortedBySeverityThenLocation()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        for (int i = 1; i < result.Violations.Count; i++)
        {
            var prev = result.Violations[i - 1];
            var curr = result.Violations[i];

            // Errors (enum value 0) before Warnings (1)
            Assert.True((int)prev.Severity <= (int)curr.Severity,
                $"Severity sort violation at {i}");

            if (prev.Severity == curr.Severity)
            {
                var pathCmp = string.CompareOrdinal(prev.FilePath, curr.FilePath);
                Assert.True(pathCmp <= 0, $"FilePath sort violation at {i}");
                if (pathCmp == 0)
                    Assert.True(prev.Line <= curr.Line, $"Line sort violation at {i}");
            }
        }
    }

    [Fact]
    public void Violations_HaveLocationAndSnippet()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        foreach (var v in result.Violations)
        {
            Assert.NotEmpty(v.FilePath);
            Assert.True(v.Line > 0);
            Assert.NotEmpty(v.Project);
            Assert.NotEmpty(v.Snippet);
            Assert.NotEmpty(v.ContainingMethod);
        }
    }
}
```

**Step 2: Run to verify they fail**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindAsyncViolationsToolTests" -v normal
```

Expected: compile errors — `FindAsyncViolationsLogic` doesn't exist.

**Step 3: Create `FindAsyncViolationsLogic.cs`**

```csharp
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class FindAsyncViolationsLogic
{
    private const int SnippetMaxLength = 80;

    public static FindAsyncViolationsResult Execute(LoadedSolution loaded, SymbolResolver source)
    {
        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);
        var violations = new List<AsyncViolation>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            if (testProjectIds.Contains(projectId))
                continue;

            var projectName = source.GetProjectName(projectId);
            var taskSymbol = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
            var taskGenericSymbol = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
            var valueTaskSymbol = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
            var valueTaskGenericSymbol = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
            var eventArgsSymbol = compilation.GetTypeByMetadataName("System.EventArgs");

            if (taskSymbol is null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                if (IsGeneratedFile(tree))
                    continue;

                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                foreach (var methodDecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
                    if (methodSymbol is null || methodSymbol.IsImplicitlyDeclared)
                        continue;

                    var containingMethodName = methodSymbol.ContainingType is null
                        ? methodSymbol.Name
                        : $"{methodSymbol.ContainingType.Name}.{methodSymbol.Name}";

                    // Pattern 4: AsyncVoid
                    if (IsAsyncVoid(methodDecl) && !IsEventHandlerShaped(methodSymbol, eventArgsSymbol))
                    {
                        var loc = methodDecl.Identifier.GetLocation();
                        violations.Add(BuildViolation(
                            AsyncViolationPattern.AsyncVoid,
                            AsyncViolationSeverity.Error,
                            loc,
                            containingMethodName,
                            projectName,
                            "async void " + methodDecl.Identifier.Text + "(...)"));
                    }

                    var body = (SyntaxNode?)methodDecl.Body ?? methodDecl.ExpressionBody;
                    if (body is null) continue;

                    var isAsync = methodDecl.Modifiers.Any(SyntaxKind.AsyncKeyword);

                    // Patterns 1-3: walk member-access + invocation expressions
                    foreach (var node in body.DescendantNodes())
                    {
                        // Pattern 1: SyncOverAsyncResult
                        if (node is MemberAccessExpressionSyntax memberAccess &&
                            memberAccess.Name.Identifier.Text == "Result" &&
                            memberAccess.Parent is not InvocationExpressionSyntax)
                        {
                            var receiverType = semanticModel.GetTypeInfo(memberAccess.Expression).Type;
                            if (IsTaskType(receiverType, taskSymbol, taskGenericSymbol))
                            {
                                violations.Add(BuildViolation(
                                    AsyncViolationPattern.SyncOverAsyncResult,
                                    AsyncViolationSeverity.Error,
                                    memberAccess.GetLocation(),
                                    containingMethodName,
                                    projectName,
                                    Snippet(memberAccess.ToString())));
                            }
                        }
                        // Patterns 2 & 3 share the InvocationExpressionSyntax shape
                        else if (node is InvocationExpressionSyntax invocation)
                        {
                            var symbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                            if (symbol is null) continue;

                            // Pattern 2: SyncOverAsyncWait
                            if (IsTaskWaitMethod(symbol, taskSymbol))
                            {
                                violations.Add(BuildViolation(
                                    AsyncViolationPattern.SyncOverAsyncWait,
                                    AsyncViolationSeverity.Error,
                                    invocation.GetLocation(),
                                    containingMethodName,
                                    projectName,
                                    Snippet(invocation.ToString())));
                            }
                            // Pattern 3: SyncOverAsyncGetAwaiterGetResult
                            else if (IsGetResultOnAwaiter(invocation, symbol))
                            {
                                violations.Add(BuildViolation(
                                    AsyncViolationPattern.SyncOverAsyncGetAwaiterGetResult,
                                    AsyncViolationSeverity.Error,
                                    invocation.GetLocation(),
                                    containingMethodName,
                                    projectName,
                                    Snippet(invocation.ToString())));
                            }
                        }
                    }

                    // Patterns 5 & 6: bare expression statement of Task-returning invocation
                    foreach (var stmt in body.DescendantNodes().OfType<ExpressionStatementSyntax>())
                    {
                        if (stmt.Expression is not InvocationExpressionSyntax invocation)
                            continue;

                        var symbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                        if (symbol is null) continue;

                        if (!IsTaskOrValueTaskType(symbol.ReturnType, taskSymbol, taskGenericSymbol, valueTaskSymbol, valueTaskGenericSymbol))
                            continue;

                        var pattern = isAsync
                            ? AsyncViolationPattern.MissingAwait
                            : AsyncViolationPattern.FireAndForget;

                        violations.Add(BuildViolation(
                            pattern,
                            AsyncViolationSeverity.Warning,
                            invocation.GetLocation(),
                            containingMethodName,
                            projectName,
                            Snippet(invocation.ToString())));
                    }
                }
            }
        }

        // Sort: severity ASC (Error=0 first), then file ASC, then line ASC.
        violations.Sort((a, b) =>
        {
            var bySeverity = ((int)a.Severity).CompareTo((int)b.Severity);
            if (bySeverity != 0) return bySeverity;
            var byPath = string.CompareOrdinal(a.FilePath, b.FilePath);
            if (byPath != 0) return byPath;
            return a.Line.CompareTo(b.Line);
        });

        var byPattern = violations
            .GroupBy(v => v.Pattern.ToString())
            .ToDictionary(g => g.Key, g => g.Count());
        var bySeverity = violations
            .GroupBy(v => v.Severity.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        var summary = new AsyncViolationSummary(
            TotalViolations: violations.Count,
            ByPattern: byPattern,
            BySeverity: bySeverity);

        return new FindAsyncViolationsResult(summary, violations);
    }

    private static bool IsAsyncVoid(MethodDeclarationSyntax methodDecl)
    {
        if (!methodDecl.Modifiers.Any(SyntaxKind.AsyncKeyword)) return false;
        return methodDecl.ReturnType is PredefinedTypeSyntax pts &&
               pts.Keyword.IsKind(SyntaxKind.VoidKeyword);
    }

    private static bool IsEventHandlerShaped(IMethodSymbol method, INamedTypeSymbol? eventArgsSymbol)
    {
        if (eventArgsSymbol is null) return false;
        if (method.Parameters.Length != 2) return false;

        // First param: typically `object`, but accept any reference-ish type.
        var firstParam = method.Parameters[0].Type;
        var firstOk = firstParam.SpecialType == SpecialType.System_Object ||
                      firstParam.TypeKind == TypeKind.Class ||
                      firstParam.TypeKind == TypeKind.Interface;
        if (!firstOk) return false;

        // Second param: EventArgs or any subclass.
        return InheritsFromOrIs(method.Parameters[1].Type, eventArgsSymbol);
    }

    private static bool InheritsFromOrIs(ITypeSymbol type, INamedTypeSymbol baseType)
    {
        var current = type as INamedTypeSymbol;
        while (current is not null)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType)) return true;
            current = current.BaseType;
        }
        return false;
    }

    private static bool IsTaskType(ITypeSymbol? type, INamedTypeSymbol taskSymbol, INamedTypeSymbol? taskGenericSymbol)
    {
        if (type is null) return false;
        if (SymbolEqualityComparer.Default.Equals(type, taskSymbol)) return true;
        if (taskGenericSymbol is not null &&
            type is INamedTypeSymbol named &&
            named.IsGenericType &&
            SymbolEqualityComparer.Default.Equals(named.ConstructedFrom, taskGenericSymbol))
        {
            return true;
        }
        return false;
    }

    private static bool IsTaskOrValueTaskType(
        ITypeSymbol type,
        INamedTypeSymbol taskSymbol,
        INamedTypeSymbol? taskGenericSymbol,
        INamedTypeSymbol? valueTaskSymbol,
        INamedTypeSymbol? valueTaskGenericSymbol)
    {
        if (SymbolEqualityComparer.Default.Equals(type, taskSymbol)) return true;
        if (valueTaskSymbol is not null && SymbolEqualityComparer.Default.Equals(type, valueTaskSymbol)) return true;

        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            var ctor = named.ConstructedFrom;
            if (taskGenericSymbol is not null &&
                SymbolEqualityComparer.Default.Equals(ctor, taskGenericSymbol)) return true;
            if (valueTaskGenericSymbol is not null &&
                SymbolEqualityComparer.Default.Equals(ctor, valueTaskGenericSymbol)) return true;
        }
        return false;
    }

    private static bool IsTaskWaitMethod(IMethodSymbol method, INamedTypeSymbol taskSymbol)
    {
        var containing = method.ContainingType?.OriginalDefinition;
        if (!SymbolEqualityComparer.Default.Equals(containing, taskSymbol)) return false;
        return method.Name is "Wait" or "WaitAll" or "WaitAny";
    }

    private static bool IsGetResultOnAwaiter(InvocationExpressionSyntax invocation, IMethodSymbol symbol)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return false;
        if (memberAccess.Name.Identifier.Text != "GetResult") return false;

        var containingType = symbol.ContainingType?.OriginalDefinition;
        if (containingType is null) return false;

        var fqn = containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal);

        return fqn is "System.Runtime.CompilerServices.TaskAwaiter"
            or "System.Runtime.CompilerServices.TaskAwaiter<TResult>"
            or "System.Runtime.CompilerServices.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter"
            or "System.Runtime.CompilerServices.ConfiguredTaskAwaitable<TResult>.ConfiguredTaskAwaiter"
            or "System.Runtime.CompilerServices.ValueTaskAwaiter"
            or "System.Runtime.CompilerServices.ValueTaskAwaiter<TResult>";
    }

    private static bool IsGeneratedFile(SyntaxTree tree)
    {
        var path = tree.FilePath;
        if (!string.IsNullOrEmpty(path))
        {
            if (path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)) return true;
        }

        var text = tree.GetText();
        var headerLength = Math.Min(text.Length, 1024);
        var header = text.GetSubText(new TextSpan(0, headerLength)).ToString();
        return header.Contains("<auto-generated>", StringComparison.OrdinalIgnoreCase);
    }

    private static string Snippet(string source)
    {
        if (source.Length <= SnippetMaxLength) return source;
        return source[..SnippetMaxLength] + "...";
    }

    private static AsyncViolation BuildViolation(
        AsyncViolationPattern pattern,
        AsyncViolationSeverity severity,
        Location location,
        string containingMethod,
        string projectName,
        string snippet)
    {
        var span = location.GetLineSpan();
        return new AsyncViolation(
            Pattern: pattern,
            Severity: severity,
            FilePath: span.Path ?? string.Empty,
            Line: span.StartLinePosition.Line + 1,
            ContainingMethod: containingMethod,
            Project: projectName,
            Snippet: snippet);
    }
}
```

**Step 4: Run all 11 tests**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindAsyncViolationsToolTests" -v normal
```

Expected: all pass.

Common debugging if a test fails:
- **Pattern not detected**: check the symbol resolution — are you using `OriginalDefinition` for generic types?
- **Negative case flagged**: re-read the negative fixture; ensure `ProperAwait`'s `await` keyword excludes it from `ExpressionStatementSyntax` walking (an `await` is part of an expression, not a bare statement).
- **Wrong severity sort**: `AsyncViolationSeverity` order — `Error` must be enum value 0.
- **AsyncFixture members missing from output**: ensure the project isn't accidentally classified as a test project.

**Step 5: Run full suite**

```bash
dotnet test
```

Expected: all green.

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Tools/FindAsyncViolationsLogic.cs \
  tests/RoslynCodeLens.Tests/Tools/FindAsyncViolationsToolTests.cs
git commit -m "feat: add FindAsyncViolationsLogic with six pattern detectors"
```

---

## Task 4: `FindAsyncViolationsTool` MCP wrapper

Thin wrapper. Auto-registered via `WithToolsFromAssembly()` in `Program.cs:35`.

**Files:**
- Create: `src/RoslynCodeLens/Tools/FindAsyncViolationsTool.cs`

**Step 1: Create the wrapper**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindAsyncViolationsTool
{
    [McpServerTool(Name = "find_async_violations")]
    [Description(
        "Detect six classes of async/await misuse across all production projects: " +
        "sync-over-async (.Result, .Wait*, GetAwaiter().GetResult()), async void " +
        "outside event handlers, missing await in async methods, and fire-and-forget " +
        "tasks. Returns a summary plus a per-violation list (severity error/warning, " +
        "location, containing method, snippet). Skips test projects and generated " +
        "code. Static analysis only — no fix suggestions.")]
    public static FindAsyncViolationsResult Execute(MultiSolutionManager manager)
    {
        manager.EnsureLoaded();
        return FindAsyncViolationsLogic.Execute(
            manager.GetLoadedSolution(),
            manager.GetResolver());
    }
}
```

**Step 2: Build the whole solution**

```bash
dotnet build
```

Expected: 0 errors. Auto-registration picks up the new `[McpServerToolType]`.

**Step 3: Run full test suite**

```bash
dotnet test
```

Expected: all green.

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Tools/FindAsyncViolationsTool.cs
git commit -m "feat: register find_async_violations MCP tool"
```

---

## Task 5: Add benchmark

**Files:**
- Modify: `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

**Step 1: Read the file** to find the existing `find_*` benchmarks.

**Step 2: Add the benchmark method** alongside existing ones (e.g. near `find_uncovered_symbols`):

```csharp
[Benchmark(Description = "find_async_violations: whole solution")]
public object FindAsyncViolations()
{
    return FindAsyncViolationsLogic.Execute(_loaded, _resolver);
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
git commit -m "bench: add find_async_violations benchmark"
```

---

## Task 6: Update SKILL.md, README, CLAUDE.md

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
- Modify: `README.md`
- Modify: `CLAUDE.md`

**Step 1: Read SKILL.md** and add the tool. Suggested entries (mirror how `find_naming_violations` and `find_uncovered_symbols` are integrated):

For the Red Flags / routing table:
> | "Are there async bugs?" / "Find sync-over-async" / "Are we using `.Result` anywhere?" | `find_async_violations` |

For the Quick Reference table:
> | `find_async_violations` | "Are there async bugs?" / "Find sync-over-async" |

For the relevant tool-listing section (likely "Diagnostics" or "Code-quality"):
> - `find_async_violations` — Detects sync-over-async (`.Result`/`.Wait()`/`GetAwaiter().GetResult()`), `async void` outside event handlers, missing awaits in async methods, and fire-and-forget tasks. Severity error/warning per violation. Static analysis; no runtime data.

NOTE: do NOT add a metadata-support row — this tool only operates on source.

**Step 2: Read README.md** and add to the Features list near `find_naming_violations`:

> - **find_async_violations** — Sync-over-async, `async void` misuse, missing awaits, fire-and-forget tasks; per-violation report with severity.

**Step 3: Update `CLAUDE.md`** — change "24 code intelligence tools" to "25 code intelligence tools".

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
git commit -m "docs: announce find_async_violations in SKILL.md, README, CLAUDE.md"
```

---

## Done

After Task 6 the branch should have ~7 commits, all tests green, the benchmark project compiling, and the tool auto-registered. From there: `/requesting-code-review` → PR.
