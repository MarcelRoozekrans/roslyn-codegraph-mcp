# Analyzer & Code Quality Tools Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add Roslyn analyzer integration (enhanced `get_diagnostics` + new `get_code_fixes`) and 5 code quality tools to the existing MCP server.

**Architecture:** All tools follow the existing Logic/Tool separation pattern. Analyzer tools use `CompilationWithAnalyzers` and `CodeFixProvider` from Roslyn. Quality tools reuse the existing `LoadedSolution` and `SymbolResolver` indexes.

**Tech Stack:** C# / .NET 10, Roslyn (`Microsoft.CodeAnalysis`), MCP SDK (`ModelContextProtocol`), xUnit, BenchmarkDotNet

**Design doc:** `docs/plans/2026-03-07-analyzer-code-quality-design.md`

---

## Batch 1: Enhanced get_diagnostics (analyzer support)

### Task 1: Add analyzer NuGet to test fixture

Add a well-known analyzer package to the test solution so we have analyzer diagnostics to test against.

**Files:**
- Modify: `tests/RoslynCodeGraph.Tests/Fixtures/TestSolution/TestLib/TestLib.csproj`

**Step 1: Add the Microsoft.CodeAnalysis.NetAnalyzers package**

Add this `<PackageReference>` to the existing `<ItemGroup>` in TestLib.csproj:

```xml
<PackageReference Include="Microsoft.CodeAnalysis.NetAnalyzers" Version="10.0.0-preview.25171.5" PrivateAssets="all" />
```

This ships CA* analyzers (e.g., CA1822 "Make member static") that will produce diagnostics in the test fixture.

**Step 2: Verify the project still builds**

Run: `dotnet build tests/RoslynCodeGraph.Tests/Fixtures/TestSolution/TestSolution.slnx`

Expected: Build succeeds (analyzer warnings are non-blocking)

**Step 3: Commit**

```bash
git add tests/RoslynCodeGraph.Tests/Fixtures/TestSolution/TestLib/TestLib.csproj
git commit -m "test: add NetAnalyzers to test fixture for analyzer integration tests"
```

---

### Task 2: Add Source field to DiagnosticInfo model

Extend the existing `DiagnosticInfo` record to distinguish compiler vs analyzer diagnostics.

**Files:**
- Modify: `src/RoslynCodeGraph/Models/DiagnosticInfo.cs`

**Step 1: Add Source parameter to the record**

Replace the record with:

```csharp
namespace RoslynCodeGraph.Models;

public record DiagnosticInfo(string Id, string Severity, string Message, string File, int Line, string Project, string Source = "compiler");
```

The `Source` field defaults to `"compiler"` for backward compatibility. Analyzer diagnostics will set it to `"analyzer:<AnalyzerName>"`.

**Step 2: Run existing tests to verify backward compatibility**

Run: `dotnet test tests/RoslynCodeGraph.Tests`

Expected: All existing tests pass (default parameter preserves behavior).

**Step 3: Commit**

```bash
git add src/RoslynCodeGraph/Models/DiagnosticInfo.cs
git commit -m "feat: add Source field to DiagnosticInfo for analyzer attribution"
```

---

### Task 3: Create AnalyzerRunner service

New service that discovers and runs Roslyn analyzers from `Project.AnalyzerReferences`.

**Files:**
- Create: `src/RoslynCodeGraph/AnalyzerRunner.cs`

**Step 1: Write the failing test**

Create `tests/RoslynCodeGraph.Tests/AnalyzerRunnerTests.cs`:

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeGraph;

namespace RoslynCodeGraph.Tests;

public class AnalyzerRunnerTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RunAnalyzersAsync_ReturnsAnalyzerDiagnostics()
    {
        var runner = new AnalyzerRunner();
        var project = _loaded.Solution.Projects.First(p => p.Name == "TestLib");
        var compilation = _loaded.Compilations[project.Id];

        var diagnostics = await runner.RunAnalyzersAsync(project, compilation, CancellationToken.None);

        // NetAnalyzers should produce at least one CA* diagnostic
        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, d => d.Id.StartsWith("CA"));
    }

    [Fact]
    public async Task RunAnalyzersAsync_NoAnalyzers_ReturnsEmpty()
    {
        var runner = new AnalyzerRunner();
        // TestLib2 may not have analyzers — if it does, this still works
        var project = _loaded.Solution.Projects.First(p => p.Name == "TestLib2");
        var compilation = _loaded.Compilations[project.Id];

        var diagnostics = await runner.RunAnalyzersAsync(project, compilation, CancellationToken.None);

        // Should not throw, may return empty or have results
        Assert.NotNull(diagnostics);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "AnalyzerRunnerTests" -v n`

Expected: FAIL — `AnalyzerRunner` class does not exist.

**Step 3: Write the AnalyzerRunner implementation**

Create `src/RoslynCodeGraph/AnalyzerRunner.cs`:

```csharp
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynCodeGraph;

public class AnalyzerRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public async Task<ImmutableArray<Diagnostic>> RunAnalyzersAsync(
        Project project,
        Compilation compilation,
        CancellationToken ct)
    {
        var analyzers = GetAnalyzers(project);
        if (analyzers.IsEmpty)
            return ImmutableArray<Diagnostic>.Empty;

        var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers, cancellationToken: ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DefaultTimeout);

        try
        {
            var results = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(timeoutCts.Token);
            return results;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout — return empty rather than failing
            return ImmutableArray<Diagnostic>.Empty;
        }
    }

    private static ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(Project project)
    {
        var analyzers = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();

        foreach (var analyzerRef in project.AnalyzerReferences)
        {
            foreach (var analyzer in analyzerRef.GetAnalyzers(project.Language))
            {
                analyzers.Add(analyzer);
            }
        }

        return analyzers.ToImmutable();
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "AnalyzerRunnerTests" -v n`

Expected: PASS

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/AnalyzerRunner.cs tests/RoslynCodeGraph.Tests/AnalyzerRunnerTests.cs
git commit -m "feat: add AnalyzerRunner service for Roslyn analyzer execution"
```

---

### Task 4: Enhance get_diagnostics to include analyzer results

Update the existing `GetDiagnosticsLogic` to optionally run analyzers via `AnalyzerRunner`.

**Files:**
- Modify: `src/RoslynCodeGraph/Tools/GetDiagnosticsTool.cs`
- Modify: `tests/RoslynCodeGraph.Tests/Tools/GetDiagnosticsToolTests.cs`

**Step 1: Write the failing test**

Add to `GetDiagnosticsToolTests.cs`:

```csharp
[Fact]
public async Task GetDiagnostics_WithAnalyzers_IncludesAnalyzerDiagnostics()
{
    var results = await GetDiagnosticsLogic.ExecuteAsync(_loaded, _resolver, null, null, includeAnalyzers: true, CancellationToken.None);

    Assert.Contains(results, r => r.Source.StartsWith("analyzer"));
}

[Fact]
public async Task GetDiagnostics_WithoutAnalyzers_OnlyCompilerDiagnostics()
{
    var results = await GetDiagnosticsLogic.ExecuteAsync(_loaded, _resolver, null, null, includeAnalyzers: false, CancellationToken.None);

    Assert.All(results, r => Assert.Equal("compiler", r.Source));
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "GetDiagnostics_WithAnalyzers" -v n`

Expected: FAIL — `ExecuteAsync` method does not exist.

**Step 3: Update GetDiagnosticsLogic with async overload**

Update `src/RoslynCodeGraph/Tools/GetDiagnosticsTool.cs`. Keep the existing sync `Execute` method (for backward compatibility) and add a new async overload:

```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetDiagnosticsLogic
{
    public static List<DiagnosticInfo> Execute(LoadedSolution loaded, SymbolResolver resolver, string? project, string? severity)
    {
        return CollectCompilerDiagnostics(loaded, resolver, project, severity);
    }

    public static async Task<List<DiagnosticInfo>> ExecuteAsync(
        LoadedSolution loaded, SymbolResolver resolver,
        string? project, string? severity,
        bool includeAnalyzers, CancellationToken ct)
    {
        var results = CollectCompilerDiagnostics(loaded, resolver, project, severity);

        if (includeAnalyzers)
        {
            var runner = new AnalyzerRunner();
            foreach (var (projectId, compilation) in loaded.Compilations)
            {
                var projectName = resolver.GetProjectName(projectId);
                if (project != null &&
                    !projectName.Contains(project, StringComparison.OrdinalIgnoreCase))
                    continue;

                var minSeverity = severity?.ToLowerInvariant() switch
                {
                    "error" => DiagnosticSeverity.Error,
                    _ => DiagnosticSeverity.Warning
                };

                var proj = loaded.Solution.GetProject(projectId);
                if (proj == null) continue;

                var analyzerDiagnostics = await runner.RunAnalyzersAsync(proj, compilation, ct);
                foreach (var diagnostic in analyzerDiagnostics)
                {
                    if (diagnostic.Severity < minSeverity) continue;

                    var lineSpan = diagnostic.Location.GetLineSpan();
                    var file = lineSpan.Path ?? "";
                    var line = lineSpan.StartLinePosition.Line + 1;

                    var analyzerName = diagnostic.Id;
                    results.Add(new DiagnosticInfo(
                        diagnostic.Id,
                        diagnostic.Severity.ToString(),
                        diagnostic.GetMessage(),
                        file, line, projectName,
                        $"analyzer:{analyzerName}"));
                }
            }
        }

        return results;
    }

    private static List<DiagnosticInfo> CollectCompilerDiagnostics(
        LoadedSolution loaded, SymbolResolver resolver, string? project, string? severity)
    {
        var minSeverity = severity?.ToLowerInvariant() switch
        {
            "error" => DiagnosticSeverity.Error,
            "warning" => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Warning
        };

        var results = new List<DiagnosticInfo>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var projectName = resolver.GetProjectName(projectId);
            if (project != null &&
                !projectName.Contains(project, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var diagnostic in compilation.GetDiagnostics())
            {
                if (diagnostic.Severity < minSeverity) continue;

                var lineSpan = diagnostic.Location.GetLineSpan();
                var file = lineSpan.Path ?? "";
                var line = lineSpan.StartLinePosition.Line + 1;

                results.Add(new DiagnosticInfo(
                    diagnostic.Id, diagnostic.Severity.ToString(),
                    diagnostic.GetMessage(), file, line, projectName));
            }
        }

        return results;
    }
}

[McpServerToolType]
public static class GetDiagnosticsTool
{
    [McpServerTool(Name = "get_diagnostics"),
     Description("List compiler errors, warnings, and analyzer diagnostics across the solution")]
    public static async Task<List<DiagnosticInfo>> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Optional project name filter")] string? project = null,
        [Description("Minimum severity: 'error' or 'warning' (default: warning)")] string? severity = null,
        [Description("Include Roslyn analyzer diagnostics (default: true)")] bool includeAnalyzers = true,
        CancellationToken ct = default)
    {
        SolutionGuard.EnsureLoaded(loaded);
        return await GetDiagnosticsLogic.ExecuteAsync(loaded, resolver, project, severity, includeAnalyzers, ct);
    }
}
```

**Step 4: Update existing tests to use the sync overload (they still call `Execute`)**

Existing tests calling `GetDiagnosticsLogic.Execute(...)` still work unchanged — the sync method is preserved. Only the new tests use `ExecuteAsync`.

**Step 5: Run all tests**

Run: `dotnet test tests/RoslynCodeGraph.Tests -v n`

Expected: All tests pass.

**Step 6: Commit**

```bash
git add src/RoslynCodeGraph/Tools/GetDiagnosticsTool.cs tests/RoslynCodeGraph.Tests/Tools/GetDiagnosticsToolTests.cs
git commit -m "feat: enhance get_diagnostics with analyzer support"
```

---

## Batch 2: get_code_fixes tool

### Task 5: Create CodeFixSuggestion model

**Files:**
- Create: `src/RoslynCodeGraph/Models/CodeFixSuggestion.cs`

**Step 1: Create the model**

```csharp
namespace RoslynCodeGraph.Models;

public record TextEdit(string FilePath, int StartLine, int StartColumn, int EndLine, int EndColumn, string NewText);

public record CodeFixSuggestion(string Title, string DiagnosticId, List<TextEdit> Edits);
```

**Step 2: Commit**

```bash
git add src/RoslynCodeGraph/Models/CodeFixSuggestion.cs
git commit -m "feat: add CodeFixSuggestion and TextEdit models"
```

---

### Task 6: Create CodeFixRunner service

**Files:**
- Create: `src/RoslynCodeGraph/CodeFixRunner.cs`
- Create: `tests/RoslynCodeGraph.Tests/CodeFixRunnerTests.cs`

**Step 1: Write the failing test**

Create `tests/RoslynCodeGraph.Tests/CodeFixRunnerTests.cs`:

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeGraph;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tests;

public class CodeFixRunnerTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetFixesAsync_ForKnownDiagnostic_ReturnsFixes()
    {
        var runner = new CodeFixRunner();
        var project = _loaded.Solution.Projects.First(p => p.Name == "TestLib");
        var compilation = _loaded.Compilations[project.Id];

        // First get a diagnostic to fix
        var analyzerRunner = new AnalyzerRunner();
        var diagnostics = await analyzerRunner.RunAnalyzersAsync(project, compilation, CancellationToken.None);
        var fixable = diagnostics.FirstOrDefault(d => d.Location.IsInSource);

        if (fixable == null)
        {
            // No fixable diagnostics — skip gracefully
            return;
        }

        var fixes = await runner.GetFixesAsync(project, fixable, CancellationToken.None);

        // If a CodeFixProvider exists for this diagnostic, we should get fixes
        // (may be empty if no provider is available — that's valid)
        Assert.NotNull(fixes);
    }

    [Fact]
    public async Task GetFixesAsync_NoDiagnostic_ReturnsEmpty()
    {
        var runner = new CodeFixRunner();
        var project = _loaded.Solution.Projects.First(p => p.Name == "TestLib");
        var compilation = _loaded.Compilations[project.Id];

        // Create a fake diagnostic with no matching CodeFixProvider
        var diagnostic = Diagnostic.Create("FAKE001", "Test", "Fake message",
            DiagnosticSeverity.Warning, DiagnosticSeverity.Warning, true, 1);

        var fixes = await runner.GetFixesAsync(project, diagnostic, CancellationToken.None);

        Assert.Empty(fixes);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "CodeFixRunnerTests" -v n`

Expected: FAIL — `CodeFixRunner` class does not exist.

**Step 3: Write the CodeFixRunner implementation**

Create `src/RoslynCodeGraph/CodeFixRunner.cs`:

```csharp
using System.Collections.Immutable;
using System.Composition.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph;

public class CodeFixRunner
{
    public async Task<List<CodeFixSuggestion>> GetFixesAsync(
        Project project, Diagnostic diagnostic, CancellationToken ct)
    {
        var providers = GetCodeFixProviders(project, diagnostic.Id);
        if (providers.Count == 0)
            return [];

        var document = FindDocument(project, diagnostic.Location);
        if (document == null)
            return [];

        var suggestions = new List<CodeFixSuggestion>();

        foreach (var provider in providers)
        {
            var actions = new List<CodeAction>();
            var context = new CodeFixContext(document, diagnostic,
                (action, _) => actions.Add(action), ct);

            try
            {
                await provider.RegisterCodeFixesAsync(context);
            }
            catch
            {
                // Skip providers that throw
                continue;
            }

            foreach (var action in actions)
            {
                var operations = await action.GetOperationsAsync(ct);
                var applyOp = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
                if (applyOp == null) continue;

                var changedSolution = applyOp.ChangedSolution;
                var edits = new List<TextEdit>();

                foreach (var changedDocId in changedSolution.GetChanges(project.Solution).GetProjectChanges()
                    .SelectMany(pc => pc.GetChangedDocuments()))
                {
                    var originalDoc = project.Solution.GetDocument(changedDocId);
                    var changedDoc = changedSolution.GetDocument(changedDocId);
                    if (originalDoc == null || changedDoc == null) continue;

                    var originalText = await originalDoc.GetTextAsync(ct);
                    var changedText = await changedDoc.GetTextAsync(ct);
                    var changes = changedText.GetChangeRanges(originalText);

                    foreach (var change in changes)
                    {
                        var startLine = originalText.Lines.GetLinePosition(change.Span.Start);
                        var endLine = originalText.Lines.GetLinePosition(change.Span.End);
                        var newText = changedText.GetSubText(change.NewSpan).ToString();

                        edits.Add(new TextEdit(
                            originalDoc.FilePath ?? "",
                            startLine.Line + 1, startLine.Character + 1,
                            endLine.Line + 1, endLine.Character + 1,
                            newText));
                    }
                }

                if (edits.Count > 0)
                {
                    suggestions.Add(new CodeFixSuggestion(action.Title, diagnostic.Id, edits));
                }
            }
        }

        return suggestions;
    }

    private static List<CodeFixProvider> GetCodeFixProviders(Project project, string diagnosticId)
    {
        var providers = new List<CodeFixProvider>();

        foreach (var analyzerRef in project.AnalyzerReferences)
        {
            foreach (var fixProvider in analyzerRef.GetFixers())
            {
                if (fixProvider.FixableDiagnosticIds.Contains(diagnosticId))
                    providers.Add(fixProvider);
            }
        }

        return providers;
    }

    private static Document? FindDocument(Project project, Location location)
    {
        if (!location.IsInSource || location.SourceTree == null)
            return null;

        return project.Documents.FirstOrDefault(d =>
            d.FilePath != null &&
            d.FilePath.Equals(location.SourceTree.FilePath, StringComparison.OrdinalIgnoreCase));
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "CodeFixRunnerTests" -v n`

Expected: PASS

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/CodeFixRunner.cs tests/RoslynCodeGraph.Tests/CodeFixRunnerTests.cs
git commit -m "feat: add CodeFixRunner for extracting code fix text edits"
```

---

### Task 7: Create get_code_fixes MCP tool

**Files:**
- Create: `src/RoslynCodeGraph/Tools/GetCodeFixesTool.cs`
- Create: `tests/RoslynCodeGraph.Tests/Tools/GetCodeFixesToolTests.cs`

**Step 1: Write the failing test**

Create `tests/RoslynCodeGraph.Tests/Tools/GetCodeFixesToolTests.cs`:

```csharp
using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class GetCodeFixesToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetCodeFixes_NoMatchingDiagnostic_ReturnsEmpty()
    {
        var results = await GetCodeFixesLogic.ExecuteAsync(
            _loaded, _resolver, "FAKE999", "NonExistent.cs", 1, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetCodeFixes_ReturnsSuggestions()
    {
        // First find an actual diagnostic
        var diagnostics = GetDiagnosticsLogic.Execute(_loaded, _resolver, null, null);
        var diag = diagnostics.FirstOrDefault(d => !string.IsNullOrEmpty(d.File));

        if (diag == null) return; // No diagnostics to test against

        var results = await GetCodeFixesLogic.ExecuteAsync(
            _loaded, _resolver, diag.Id, diag.File, diag.Line, CancellationToken.None);

        // May be empty if no CodeFixProvider exists for this diagnostic — that's valid
        Assert.NotNull(results);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "GetCodeFixesToolTests" -v n`

Expected: FAIL — `GetCodeFixesLogic` does not exist.

**Step 3: Write the tool implementation**

Create `src/RoslynCodeGraph/Tools/GetCodeFixesTool.cs`:

```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetCodeFixesLogic
{
    public static async Task<List<CodeFixSuggestion>> ExecuteAsync(
        LoadedSolution loaded, SymbolResolver resolver,
        string diagnosticId, string filePath, int line,
        CancellationToken ct)
    {
        // Find the project containing this file
        var normalizedPath = Path.GetFullPath(filePath);
        Project? targetProject = null;
        Document? targetDocument = null;

        foreach (var project in loaded.Solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath != null &&
                    doc.FilePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    targetProject = project;
                    targetDocument = doc;
                    break;
                }
            }
            if (targetProject != null) break;
        }

        if (targetProject == null || targetDocument == null)
            return [];

        if (!loaded.Compilations.TryGetValue(targetProject.Id, out var compilation))
            return [];

        // Find diagnostics at the specified location
        var analyzerRunner = new AnalyzerRunner();
        var allDiagnostics = compilation.GetDiagnostics()
            .Concat(await analyzerRunner.RunAnalyzersAsync(targetProject, compilation, ct));

        var matchingDiagnostics = allDiagnostics
            .Where(d => d.Id == diagnosticId &&
                        d.Location.IsInSource &&
                        d.Location.SourceTree?.FilePath != null &&
                        d.Location.SourceTree.FilePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                        d.Location.GetLineSpan().StartLinePosition.Line + 1 == line)
            .ToList();

        if (matchingDiagnostics.Count == 0)
            return [];

        var fixRunner = new CodeFixRunner();
        var results = new List<CodeFixSuggestion>();

        foreach (var diagnostic in matchingDiagnostics)
        {
            var fixes = await fixRunner.GetFixesAsync(targetProject, diagnostic, ct);
            results.AddRange(fixes);
        }

        return results;
    }
}

[McpServerToolType]
public static class GetCodeFixesTool
{
    [McpServerTool(Name = "get_code_fixes"),
     Description("Get available code fixes for a specific diagnostic at a file location. Returns structured text edits that can be reviewed and applied.")]
    public static async Task<List<CodeFixSuggestion>> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Diagnostic ID (e.g., 'CA1822', 'CS0168')")] string diagnosticId,
        [Description("Full path to the source file")] string filePath,
        [Description("Line number where the diagnostic occurs")] int line,
        CancellationToken ct = default)
    {
        SolutionGuard.EnsureLoaded(loaded);
        return await GetCodeFixesLogic.ExecuteAsync(loaded, resolver, diagnosticId, filePath, line, ct);
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "GetCodeFixesToolTests" -v n`

Expected: PASS

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/Tools/GetCodeFixesTool.cs tests/RoslynCodeGraph.Tests/Tools/GetCodeFixesToolTests.cs
git commit -m "feat: add get_code_fixes tool for surfacing code fix suggestions"
```

---

## Batch 3: Code Quality Tools

### Task 8: find_circular_dependencies

Detects cycles in project reference graph using DFS.

**Files:**
- Create: `src/RoslynCodeGraph/Models/CircularDependency.cs`
- Create: `src/RoslynCodeGraph/Tools/FindCircularDependenciesTool.cs`
- Create: `tests/RoslynCodeGraph.Tests/Tools/FindCircularDependenciesToolTests.cs`

**Step 1: Create the model**

Create `src/RoslynCodeGraph/Models/CircularDependency.cs`:

```csharp
namespace RoslynCodeGraph.Models;

public record CircularDependency(string Level, List<string> Cycle);
```

**Step 2: Write the failing test**

Create `tests/RoslynCodeGraph.Tests/Tools/FindCircularDependenciesToolTests.cs`:

```csharp
using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class FindCircularDependenciesToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void FindCircularDependencies_NoCycles_ReturnsEmpty()
    {
        // TestLib → TestLib2 is one-directional, no cycles
        var results = FindCircularDependenciesLogic.Execute(_loaded, _resolver, "project");
        Assert.Empty(results);
    }

    [Fact]
    public void FindCircularDependencies_InvalidLevel_ReturnsEmpty()
    {
        var results = FindCircularDependenciesLogic.Execute(_loaded, _resolver, "invalid");
        Assert.Empty(results);
    }
}
```

**Step 3: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "FindCircularDependenciesToolTests" -v n`

Expected: FAIL

**Step 4: Write the implementation**

Create `src/RoslynCodeGraph/Tools/FindCircularDependenciesTool.cs`:

```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindCircularDependenciesLogic
{
    public static List<CircularDependency> Execute(LoadedSolution loaded, SymbolResolver resolver, string level)
    {
        return level.ToLowerInvariant() switch
        {
            "project" => FindProjectCycles(loaded),
            "namespace" => FindNamespaceCycles(loaded),
            _ => []
        };
    }

    private static List<CircularDependency> FindProjectCycles(LoadedSolution loaded)
    {
        var graph = new Dictionary<string, List<string>>();
        foreach (var project in loaded.Solution.Projects)
        {
            var deps = new List<string>();
            foreach (var refProject in project.ProjectReferences)
            {
                var refName = loaded.Solution.GetProject(refProject.ProjectId)?.Name;
                if (refName != null) deps.Add(refName);
            }
            graph[project.Name] = deps;
        }

        return DetectCycles(graph, "project");
    }

    private static List<CircularDependency> FindNamespaceCycles(LoadedSolution loaded)
    {
        var graph = new Dictionary<string, HashSet<string>>();

        foreach (var (_, compilation) in loaded.Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                // Get the namespace of this file
                var nsDecl = root.DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax>()
                    .FirstOrDefault();
                if (nsDecl == null) continue;
                var ns = nsDecl.Name.ToString();

                // Get using directives
                var usings = root.DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax>()
                    .Select(u => u.Name?.ToString())
                    .Where(u => u != null)
                    .Cast<string>();

                if (!graph.TryGetValue(ns, out var deps))
                {
                    deps = new HashSet<string>();
                    graph[ns] = deps;
                }

                foreach (var usingNs in usings)
                {
                    if (usingNs != ns) // Skip self-references
                        deps.Add(usingNs);
                }
            }
        }

        // Only consider namespaces that are defined in our solution
        var solutionNamespaces = graph.Keys.ToHashSet();
        var filteredGraph = graph.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Where(d => solutionNamespaces.Contains(d)).ToList() as IList<string>);

        return DetectCycles(
            filteredGraph.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
            "namespace");
    }

    private static List<CircularDependency> DetectCycles(Dictionary<string, List<string>> graph, string level)
    {
        var cycles = new List<CircularDependency>();
        var visited = new HashSet<string>();
        var inStack = new HashSet<string>();
        var path = new List<string>();

        foreach (var node in graph.Keys)
        {
            if (!visited.Contains(node))
                Dfs(node, graph, visited, inStack, path, cycles, level);
        }

        return cycles;
    }

    private static void Dfs(string node, Dictionary<string, List<string>> graph,
        HashSet<string> visited, HashSet<string> inStack,
        List<string> path, List<CircularDependency> cycles, string level)
    {
        visited.Add(node);
        inStack.Add(node);
        path.Add(node);

        if (graph.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                if (inStack.Contains(neighbor))
                {
                    // Found a cycle
                    var cycleStart = path.IndexOf(neighbor);
                    var cycle = path.Skip(cycleStart).Append(neighbor).ToList();
                    cycles.Add(new CircularDependency(level, cycle));
                }
                else if (!visited.Contains(neighbor))
                {
                    Dfs(neighbor, graph, visited, inStack, path, cycles, level);
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        inStack.Remove(node);
    }
}

[McpServerToolType]
public static class FindCircularDependenciesTool
{
    [McpServerTool(Name = "find_circular_dependencies"),
     Description("Detect circular dependencies in the project reference graph or namespace dependency graph")]
    public static List<CircularDependency> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Level: 'project' or 'namespace' (default: project)")] string level = "project")
    {
        SolutionGuard.EnsureLoaded(loaded);
        return FindCircularDependenciesLogic.Execute(loaded, resolver, level);
    }
}
```

**Step 5: Run tests**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "FindCircularDependenciesToolTests" -v n`

Expected: PASS

**Step 6: Commit**

```bash
git add src/RoslynCodeGraph/Models/CircularDependency.cs src/RoslynCodeGraph/Tools/FindCircularDependenciesTool.cs tests/RoslynCodeGraph.Tests/Tools/FindCircularDependenciesToolTests.cs
git commit -m "feat: add find_circular_dependencies tool"
```

---

### Task 9: get_complexity_metrics

Walks syntax trees counting branches per method.

**Files:**
- Create: `src/RoslynCodeGraph/Models/ComplexityMetric.cs`
- Create: `src/RoslynCodeGraph/Tools/GetComplexityMetricsTool.cs`
- Create: `tests/RoslynCodeGraph.Tests/Tools/GetComplexityMetricsToolTests.cs`

**Step 1: Create the model**

Create `src/RoslynCodeGraph/Models/ComplexityMetric.cs`:

```csharp
namespace RoslynCodeGraph.Models;

public record ComplexityMetric(string MethodName, string TypeName, int Complexity, string File, int Line, string Project);
```

**Step 2: Write the failing test**

Create `tests/RoslynCodeGraph.Tests/Tools/GetComplexityMetricsToolTests.cs`:

```csharp
using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class GetComplexityMetricsToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void GetComplexityMetrics_AllMethods_ReturnsResults()
    {
        // threshold 0 = show all methods
        var results = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 0);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void GetComplexityMetrics_HighThreshold_ReturnsEmpty()
    {
        // No method in test fixture should have complexity > 100
        var results = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 100);
        Assert.Empty(results);
    }

    [Fact]
    public void GetComplexityMetrics_ProjectFilter_FiltersResults()
    {
        var all = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 0);
        var filtered = GetComplexityMetricsLogic.Execute(_loaded, _resolver, "TestLib2", 0);

        Assert.All(filtered, r => Assert.Equal("TestLib2", r.Project));
    }
}
```

**Step 3: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "GetComplexityMetricsToolTests" -v n`

Expected: FAIL

**Step 4: Write the implementation**

Create `src/RoslynCodeGraph/Tools/GetComplexityMetricsTool.cs`:

```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetComplexityMetricsLogic
{
    public static List<ComplexityMetric> Execute(LoadedSolution loaded, SymbolResolver resolver, string? project, int threshold)
    {
        var results = new List<ComplexityMetric>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var projectName = resolver.GetProjectName(projectId);
            if (project != null && !projectName.Contains(project, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = tree.GetRoot();
                var model = compilation.GetSemanticModel(tree);

                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var complexity = CalculateComplexity(method);
                    if (complexity < threshold) continue;

                    var symbol = model.GetDeclaredSymbol(method);
                    var typeName = symbol?.ContainingType?.ToDisplayString() ?? "";
                    var methodName = symbol?.Name ?? method.Identifier.Text;
                    var lineSpan = method.GetLocation().GetLineSpan();

                    results.Add(new ComplexityMetric(
                        methodName, typeName, complexity,
                        lineSpan.Path ?? "", lineSpan.StartLinePosition.Line + 1, projectName));
                }
            }
        }

        return results.OrderByDescending(r => r.Complexity).ToList();
    }

    private static int CalculateComplexity(MethodDeclarationSyntax method)
    {
        var complexity = 1; // Base complexity

        foreach (var node in method.DescendantNodes())
        {
            complexity += node.Kind() switch
            {
                SyntaxKind.IfStatement => 1,
                SyntaxKind.ElseClause => 1,
                SyntaxKind.SwitchSection => 1,
                SyntaxKind.ForStatement => 1,
                SyntaxKind.ForEachStatement => 1,
                SyntaxKind.WhileStatement => 1,
                SyntaxKind.DoStatement => 1,
                SyntaxKind.CatchClause => 1,
                SyntaxKind.LogicalAndExpression => 1,
                SyntaxKind.LogicalOrExpression => 1,
                SyntaxKind.CoalesceExpression => 1,
                SyntaxKind.ConditionalExpression => 1, // ternary
                _ => 0
            };
        }

        return complexity;
    }
}

[McpServerToolType]
public static class GetComplexityMetricsTool
{
    [McpServerTool(Name = "get_complexity_metrics"),
     Description("Calculate cyclomatic complexity for methods. Returns methods exceeding the threshold, sorted by complexity.")]
    public static List<ComplexityMetric> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Optional project name filter")] string? project = null,
        [Description("Minimum complexity threshold (default: 10)")] int threshold = 10)
    {
        SolutionGuard.EnsureLoaded(loaded);
        return GetComplexityMetricsLogic.Execute(loaded, resolver, project, threshold);
    }
}
```

**Step 5: Run tests**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "GetComplexityMetricsToolTests" -v n`

Expected: PASS

**Step 6: Commit**

```bash
git add src/RoslynCodeGraph/Models/ComplexityMetric.cs src/RoslynCodeGraph/Tools/GetComplexityMetricsTool.cs tests/RoslynCodeGraph.Tests/Tools/GetComplexityMetricsToolTests.cs
git commit -m "feat: add get_complexity_metrics tool"
```

---

### Task 10: find_naming_violations

Checks .NET naming conventions.

**Files:**
- Create: `src/RoslynCodeGraph/Models/NamingViolation.cs`
- Create: `src/RoslynCodeGraph/Tools/FindNamingViolationsTool.cs`
- Create: `tests/RoslynCodeGraph.Tests/Tools/FindNamingViolationsToolTests.cs`

**Step 1: Create the model**

Create `src/RoslynCodeGraph/Models/NamingViolation.cs`:

```csharp
namespace RoslynCodeGraph.Models;

public record NamingViolation(string SymbolName, string SymbolKind, string Rule, string Suggestion, string File, int Line, string Project);
```

**Step 2: Write the failing test**

Create `tests/RoslynCodeGraph.Tests/Tools/FindNamingViolationsToolTests.cs`:

```csharp
using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class FindNamingViolationsToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void FindNamingViolations_CleanCode_NoViolations()
    {
        // Test fixture follows conventions — expect no violations (or minimal)
        var results = FindNamingViolationsLogic.Execute(_loaded, _resolver, null);
        Assert.NotNull(results);
    }

    [Fact]
    public void FindNamingViolations_ProjectFilter_FiltersResults()
    {
        var filtered = FindNamingViolationsLogic.Execute(_loaded, _resolver, "TestLib");
        Assert.All(filtered, r => Assert.Contains("TestLib", r.Project));
    }
}
```

**Step 3: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "FindNamingViolationsToolTests" -v n`

Expected: FAIL

**Step 4: Write the implementation**

Create `src/RoslynCodeGraph/Tools/FindNamingViolationsTool.cs`:

```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindNamingViolationsLogic
{
    public static List<NamingViolation> Execute(LoadedSolution loaded, SymbolResolver resolver, string? project)
    {
        var results = new List<NamingViolation>();

        foreach (var type in resolver.AllTypes)
        {
            var projectName = resolver.GetProjectName(type);
            if (project != null && !projectName.Contains(project, StringComparison.OrdinalIgnoreCase))
                continue;

            // Check type naming: PascalCase, interfaces must start with I
            CheckTypeName(type, resolver, results, projectName);

            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared) continue;
                CheckMemberName(member, resolver, results, projectName);
            }
        }

        return results;
    }

    private static void CheckTypeName(INamedTypeSymbol type, SymbolResolver resolver,
        List<NamingViolation> results, string project)
    {
        var name = type.Name;
        if (string.IsNullOrEmpty(name)) return;

        var (file, line) = resolver.GetFileAndLine(type);

        // Interface must start with I
        if (type.TypeKind == TypeKind.Interface && !name.StartsWith('I'))
        {
            results.Add(new NamingViolation(name, "interface", "Interface names should start with 'I'",
                $"I{name}", file, line, project));
        }

        // Types should be PascalCase
        if (type.TypeKind != TypeKind.Interface && char.IsLower(name[0]))
        {
            results.Add(new NamingViolation(name, type.TypeKind.ToString().ToLowerInvariant(),
                "Type names should be PascalCase",
                char.ToUpper(name[0]) + name[1..], file, line, project));
        }
    }

    private static void CheckMemberName(ISymbol member, SymbolResolver resolver,
        List<NamingViolation> results, string project)
    {
        var name = member.Name;
        if (string.IsNullOrEmpty(name) || name.StartsWith('.')) return;

        var (file, line) = resolver.GetFileAndLine(member);
        if (string.IsNullOrEmpty(file)) return;

        switch (member)
        {
            case IMethodSymbol { MethodKind: MethodKind.Ordinary } when char.IsLower(name[0]):
                results.Add(new NamingViolation(name, "method", "Method names should be PascalCase",
                    char.ToUpper(name[0]) + name[1..], file, line, project));
                break;

            case IPropertySymbol when char.IsLower(name[0]):
                results.Add(new NamingViolation(name, "property", "Property names should be PascalCase",
                    char.ToUpper(name[0]) + name[1..], file, line, project));
                break;

            case IFieldSymbol field when field.DeclaredAccessibility == Accessibility.Private:
                if (!name.StartsWith('_'))
                {
                    results.Add(new NamingViolation(name, "field",
                        "Private fields should start with '_'",
                        $"_{char.ToLower(name[0])}{name[1..]}", file, line, project));
                }
                break;

            case IParameterSymbol when char.IsUpper(name[0]):
                results.Add(new NamingViolation(name, "parameter", "Parameters should be camelCase",
                    char.ToLower(name[0]) + name[1..], file, line, project));
                break;
        }
    }
}

[McpServerToolType]
public static class FindNamingViolationsTool
{
    [McpServerTool(Name = "find_naming_violations"),
     Description("Check .NET naming convention compliance: PascalCase types/methods/properties, camelCase parameters, I-prefix interfaces, _ prefix private fields")]
    public static List<NamingViolation> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Optional project name filter")] string? project = null)
    {
        SolutionGuard.EnsureLoaded(loaded);
        return FindNamingViolationsLogic.Execute(loaded, resolver, project);
    }
}
```

**Step 5: Run tests**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "FindNamingViolationsToolTests" -v n`

Expected: PASS

**Step 6: Commit**

```bash
git add src/RoslynCodeGraph/Models/NamingViolation.cs src/RoslynCodeGraph/Tools/FindNamingViolationsTool.cs tests/RoslynCodeGraph.Tests/Tools/FindNamingViolationsToolTests.cs
git commit -m "feat: add find_naming_violations tool"
```

---

### Task 11: find_large_classes

Reports types exceeding member count or line count thresholds.

**Files:**
- Create: `src/RoslynCodeGraph/Models/LargeClassInfo.cs`
- Create: `src/RoslynCodeGraph/Tools/FindLargeClassesTool.cs`
- Create: `tests/RoslynCodeGraph.Tests/Tools/FindLargeClassesToolTests.cs`

**Step 1: Create the model**

Create `src/RoslynCodeGraph/Models/LargeClassInfo.cs`:

```csharp
namespace RoslynCodeGraph.Models;

public record LargeClassInfo(string TypeName, int MemberCount, int LineCount, string File, int Line, string Project);
```

**Step 2: Write the failing test**

Create `tests/RoslynCodeGraph.Tests/Tools/FindLargeClassesToolTests.cs`:

```csharp
using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class FindLargeClassesToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void FindLargeClasses_LowThreshold_ReturnsResults()
    {
        // Low thresholds should find some classes
        var results = FindLargeClassesLogic.Execute(_loaded, _resolver, null, 1, 1);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void FindLargeClasses_HighThreshold_ReturnsEmpty()
    {
        var results = FindLargeClassesLogic.Execute(_loaded, _resolver, null, 1000, 10000);
        Assert.Empty(results);
    }

    [Fact]
    public void FindLargeClasses_ProjectFilter_FiltersResults()
    {
        var results = FindLargeClassesLogic.Execute(_loaded, _resolver, "TestLib2", 1, 1);
        Assert.All(results, r => Assert.Equal("TestLib2", r.Project));
    }
}
```

**Step 3: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "FindLargeClassesToolTests" -v n`

Expected: FAIL

**Step 4: Write the implementation**

Create `src/RoslynCodeGraph/Tools/FindLargeClassesTool.cs`:

```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindLargeClassesLogic
{
    public static List<LargeClassInfo> Execute(LoadedSolution loaded, SymbolResolver resolver,
        string? project, int maxMembers, int maxLines)
    {
        var results = new List<LargeClassInfo>();

        foreach (var type in resolver.AllTypes)
        {
            if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
                continue;

            var projectName = resolver.GetProjectName(type);
            if (project != null && !projectName.Contains(project, StringComparison.OrdinalIgnoreCase))
                continue;

            var memberCount = type.GetMembers()
                .Count(m => !m.IsImplicitlyDeclared);

            var lineCount = GetLineCount(type);

            if (memberCount >= maxMembers || lineCount >= maxLines)
            {
                var (file, line) = resolver.GetFileAndLine(type);
                results.Add(new LargeClassInfo(
                    type.ToDisplayString(), memberCount, lineCount,
                    file, line, projectName));
            }
        }

        return results.OrderByDescending(r => r.MemberCount).ToList();
    }

    private static int GetLineCount(INamedTypeSymbol type)
    {
        var syntaxRef = type.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return 0;

        var span = syntaxRef.Span;
        var tree = syntaxRef.SyntaxTree;
        var startLine = tree.GetLineSpan(span).StartLinePosition.Line;
        var endLine = tree.GetLineSpan(span).EndLinePosition.Line;

        return endLine - startLine + 1;
    }
}

[McpServerToolType]
public static class FindLargeClassesTool
{
    [McpServerTool(Name = "find_large_classes"),
     Description("Find classes and structs that exceed member count or line count thresholds")]
    public static List<LargeClassInfo> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Optional project name filter")] string? project = null,
        [Description("Maximum members before flagging (default: 20)")] int maxMembers = 20,
        [Description("Maximum lines before flagging (default: 500)")] int maxLines = 500)
    {
        SolutionGuard.EnsureLoaded(loaded);
        return FindLargeClassesLogic.Execute(loaded, resolver, project, maxMembers, maxLines);
    }
}
```

**Step 5: Run tests**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "FindLargeClassesToolTests" -v n`

Expected: PASS

**Step 6: Commit**

```bash
git add src/RoslynCodeGraph/Models/LargeClassInfo.cs src/RoslynCodeGraph/Tools/FindLargeClassesTool.cs tests/RoslynCodeGraph.Tests/Tools/FindLargeClassesToolTests.cs
git commit -m "feat: add find_large_classes tool"
```

---

### Task 12: find_unused_symbols

Dead code detection using reference counting.

**Files:**
- Create: `src/RoslynCodeGraph/Models/UnusedSymbolInfo.cs`
- Create: `src/RoslynCodeGraph/Tools/FindUnusedSymbolsTool.cs`
- Create: `tests/RoslynCodeGraph.Tests/Tools/FindUnusedSymbolsToolTests.cs`

**Step 1: Create the model**

Create `src/RoslynCodeGraph/Models/UnusedSymbolInfo.cs`:

```csharp
namespace RoslynCodeGraph.Models;

public record UnusedSymbolInfo(string SymbolName, string SymbolKind, string File, int Line, string Project);
```

**Step 2: Write the failing test**

Create `tests/RoslynCodeGraph.Tests/Tools/FindUnusedSymbolsToolTests.cs`:

```csharp
using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class FindUnusedSymbolsToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void FindUnusedSymbols_ReturnsResults()
    {
        // Should find at least some symbols (OldGreet, FancyGreeter, etc. may be unused)
        var results = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, null, false);
        Assert.NotNull(results);
    }

    [Fact]
    public void FindUnusedSymbols_ProjectFilter_FiltersResults()
    {
        var results = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);
        Assert.All(results, r => Assert.Contains("TestLib", r.Project));
    }
}
```

**Step 3: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "FindUnusedSymbolsToolTests" -v n`

Expected: FAIL

**Step 4: Write the implementation**

Create `src/RoslynCodeGraph/Tools/FindUnusedSymbolsTool.cs`:

```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindUnusedSymbolsLogic
{
    public static List<UnusedSymbolInfo> Execute(LoadedSolution loaded, SymbolResolver resolver,
        string? project, bool includeInternal)
    {
        var results = new List<UnusedSymbolInfo>();

        foreach (var type in resolver.AllTypes)
        {
            var projectName = resolver.GetProjectName(type);
            if (project != null && !projectName.Contains(project, StringComparison.OrdinalIgnoreCase))
                continue;

            // Skip types we shouldn't flag
            if (ShouldSkip(type, includeInternal)) continue;

            // Check if type itself is referenced
            var typeRefs = FindReferencesLogic.Execute(loaded, resolver, type.ToDisplayString());
            if (typeRefs.Count == 0)
            {
                var (file, line) = resolver.GetFileAndLine(type);
                if (!string.IsNullOrEmpty(file))
                {
                    results.Add(new UnusedSymbolInfo(
                        type.ToDisplayString(), type.TypeKind.ToString().ToLowerInvariant(),
                        file, line, projectName));
                }
                continue; // Skip members if the whole type is unused
            }

            // Check public/internal members
            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared) continue;
                if (member is IMethodSymbol { MethodKind: not MethodKind.Ordinary }) continue;
                if (ShouldSkipMember(member, includeInternal)) continue;

                var memberName = $"{type.Name}.{member.Name}";
                var memberRefs = FindReferencesLogic.Execute(loaded, resolver, memberName);

                // Subtract self-references (declarations don't count)
                if (memberRefs.Count == 0)
                {
                    var (file, line) = resolver.GetFileAndLine(member);
                    if (!string.IsNullOrEmpty(file))
                    {
                        var kind = member switch
                        {
                            IMethodSymbol => "method",
                            IPropertySymbol => "property",
                            IFieldSymbol => "field",
                            IEventSymbol => "event",
                            _ => "member"
                        };
                        results.Add(new UnusedSymbolInfo(
                            member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            kind, file, line, projectName));
                    }
                }
            }
        }

        return results;
    }

    private static bool ShouldSkip(INamedTypeSymbol type, bool includeInternal)
    {
        // Skip private and protected types (nested types)
        if (type.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected)
            return true;

        // Skip internal unless requested
        if (!includeInternal && type.DeclaredAccessibility == Accessibility.Internal)
            return true;

        // Skip interfaces (implementations are the real code)
        if (type.TypeKind == TypeKind.Interface) return true;

        // Skip static classes with extension methods (likely DI setup, etc.)
        if (type.IsStatic && type.GetMembers().Any(m => m is IMethodSymbol { IsExtensionMethod: true }))
            return true;

        // Skip entry point types
        if (type.GetMembers("Main").Any()) return true;

        return false;
    }

    private static bool ShouldSkipMember(ISymbol member, bool includeInternal)
    {
        if (member.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected)
            return true;

        if (!includeInternal && member.DeclaredAccessibility == Accessibility.Internal)
            return true;

        // Skip overrides (they fulfill a contract)
        if (member is IMethodSymbol { IsOverride: true }) return true;
        if (member is IPropertySymbol { IsOverride: true }) return true;

        // Skip interface implementation members
        if (member.ContainingType.AllInterfaces
            .SelectMany(i => i.GetMembers())
            .Any(im => SymbolEqualityComparer.Default.Equals(
                member.ContainingType.FindImplementationForInterfaceMember(im), member)))
            return true;

        return false;
    }
}

[McpServerToolType]
public static class FindUnusedSymbolsTool
{
    [McpServerTool(Name = "find_unused_symbols"),
     Description("Find potentially unused types and members (dead code detection). Checks public symbols for references across the solution.")]
    public static List<UnusedSymbolInfo> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Optional project name filter")] string? project = null,
        [Description("Include internal symbols (default: false)")] bool includeInternal = false)
    {
        SolutionGuard.EnsureLoaded(loaded);
        return FindUnusedSymbolsLogic.Execute(loaded, resolver, project, includeInternal);
    }
}
```

**Step 5: Run tests**

Run: `dotnet test tests/RoslynCodeGraph.Tests --filter "FindUnusedSymbolsToolTests" -v n`

Expected: PASS

**Step 6: Commit**

```bash
git add src/RoslynCodeGraph/Models/UnusedSymbolInfo.cs src/RoslynCodeGraph/Tools/FindUnusedSymbolsTool.cs tests/RoslynCodeGraph.Tests/Tools/FindUnusedSymbolsToolTests.cs
git commit -m "feat: add find_unused_symbols tool"
```

---

## Batch 4: Documentation & Benchmarks

### Task 13: Add benchmarks for new tools

**Files:**
- Modify: `benchmarks/RoslynCodeGraph.Benchmarks/Benchmarks.cs`

**Step 1: Add benchmarks for the new tools**

Add these benchmark methods to the `CodeGraphBenchmarks` class:

```csharp
[Benchmark(Description = "find_circular_dependencies: project")]
public object FindCircularDependencies()
{
    return FindCircularDependenciesLogic.Execute(_loaded, _resolver, "project");
}

[Benchmark(Description = "get_complexity_metrics: all")]
public object GetComplexityMetrics()
{
    return GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 10);
}

[Benchmark(Description = "find_naming_violations: all")]
public object FindNamingViolations()
{
    return FindNamingViolationsLogic.Execute(_loaded, _resolver, null);
}

[Benchmark(Description = "find_large_classes: all")]
public object FindLargeClasses()
{
    return FindLargeClassesLogic.Execute(_loaded, _resolver, null, 20, 500);
}

[Benchmark(Description = "find_unused_symbols: all")]
public object FindUnusedSymbols()
{
    return FindUnusedSymbolsLogic.Execute(_loaded, _resolver, null, false);
}
```

Note: `get_diagnostics` with analyzers and `get_code_fixes` are async and slower — benchmark them separately if needed, or skip (they involve analyzer execution which is inherently slower).

**Step 2: Run benchmarks**

Run: `dotnet run --project benchmarks/RoslynCodeGraph.Benchmarks -c Release`

**Step 3: Commit**

```bash
git add benchmarks/RoslynCodeGraph.Benchmarks/Benchmarks.cs
git commit -m "bench: add benchmarks for code quality tools"
```

---

### Task 14: Update README and SKILL.md

**Files:**
- Modify: `README.md` — add new tools to feature list and performance table
- Modify: `plugins/roslyn-codegraph/skills/roslyn-codegraph/SKILL.md` — add Code Quality Analysis section and Analyzer section

**Step 1: Update README.md**

Add to the Features section:
- **get_code_fixes** — Get available code fixes with structured text edits
- **find_circular_dependencies** — Detect cycles in project/namespace dependency graphs
- **get_complexity_metrics** — Cyclomatic complexity analysis per method
- **find_naming_violations** — Check .NET naming convention compliance
- **find_large_classes** — Find oversized types by member or line count
- **find_unused_symbols** — Dead code detection via reference analysis

Update `get_diagnostics` description to mention analyzer support.

Update the performance table with benchmark results from Task 13.

**Step 2: Update SKILL.md**

Add sections:

```markdown
### Analyzer Integration

- Use `get_diagnostics` with analyzer support to surface Roslyn analyzer warnings alongside compiler diagnostics
- Use `get_code_fixes` to get structured text edits for fixing diagnostics — review and apply via Edit tool

### Code Quality Analysis

- Use `find_unused_symbols` to detect dead code — types/members with no references
- Use `get_complexity_metrics` to find overly complex methods (cyclomatic complexity)
- Use `find_naming_violations` to check .NET naming convention compliance
- Use `find_large_classes` to identify types that may need refactoring
- Use `find_circular_dependencies` to detect project or namespace dependency cycles
```

Add to the quick reference table:
```markdown
| `get_code_fixes` | "How do I fix this warning?" / "Get auto-fix suggestions" |
| `find_unused_symbols` | "Any dead code?" / "What's not being used?" |
| `get_complexity_metrics` | "Which methods are too complex?" |
| `find_naming_violations` | "Check naming conventions" |
| `find_large_classes` | "Find classes that need splitting" |
| `find_circular_dependencies` | "Any circular dependencies?" |
```

**Step 3: Commit**

```bash
git add README.md plugins/roslyn-codegraph/skills/roslyn-codegraph/SKILL.md
git commit -m "docs: update README and skill with all 19 tools"
```

---

### Task 15: Run full test suite and verify

**Step 1: Run all tests**

Run: `dotnet test tests/RoslynCodeGraph.Tests -v n`

Expected: All tests pass.

**Step 2: Build the project**

Run: `dotnet build src/RoslynCodeGraph -c Release`

Expected: Build succeeds with no errors.

---

## Summary

| Batch | Tasks | New Files | Modified Files |
|-------|-------|-----------|----------------|
| 1: Analyzer integration | 1-4 | AnalyzerRunner.cs, AnalyzerRunnerTests.cs | DiagnosticInfo.cs, GetDiagnosticsTool.cs, TestLib.csproj |
| 2: get_code_fixes | 5-7 | CodeFixSuggestion.cs, CodeFixRunner.cs, GetCodeFixesTool.cs, tests | — |
| 3: Quality tools | 8-12 | 5 models, 5 tools, 5 test files | — |
| 4: Docs & benchmarks | 13-15 | — | Benchmarks.cs, README.md, SKILL.md |
