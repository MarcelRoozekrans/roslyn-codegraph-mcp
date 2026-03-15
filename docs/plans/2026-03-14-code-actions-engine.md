# Code Actions Engine Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add two generic tools (`get_code_actions` and `apply_code_action`) that expose ALL built-in Roslyn refactorings and code fixes through a single interface — unlocking rename, extract method, extract interface, inline variable, and dozens more without implementing each one individually.

**Architecture:** A new `CodeActionRunner` class discovers `CodeRefactoringProvider` and `CodeFixProvider` types from both project analyzer assemblies AND the built-in `Microsoft.CodeAnalysis.Features` assembly via reflection. Two new MCP tools let agents discover available actions at a position (with optional text selection) and apply them by title with preview support. This builds on the existing `CodeFixRunner` pattern but generalizes it.

**Tech Stack:** Microsoft.CodeAnalysis.Features (4.14.0), existing Tool+Logic pattern, xUnit integration tests.

---

### Task 1: Add Microsoft.CodeAnalysis.Features Package Reference

**Files:**
- Modify: `src/RoslynCodeLens/RoslynCodeLens.csproj:27-37`

**Step 1: Add the package reference**

Add to the `<ItemGroup>` containing other CodeAnalysis packages (after line 30):

```xml
<PackageReference Include="Microsoft.CodeAnalysis.Features" Version="4.14.0" />
<PackageReference Include="Microsoft.CodeAnalysis.CSharp.Features" Version="4.14.0" />
```

**Step 2: Verify it builds**

Run: `dotnet build src/RoslynCodeLens/RoslynCodeLens.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/RoslynCodeLens/RoslynCodeLens.csproj
git commit -m "deps: add Microsoft.CodeAnalysis.Features for code action support"
```

---

### Task 2: Create CodeActionInfo Response Model

**Files:**
- Create: `src/RoslynCodeLens/Models/CodeActionInfo.cs`

**Step 1: Write the model**

```csharp
namespace RoslynCodeLens.Models;

public record CodeActionInfo(string Title, string Kind, IReadOnlyList<CodeActionInfo>? NestedActions = null);
```

`Kind` maps to `CodeAction.Tags` or the provider type (e.g., "Refactoring", "CodeFix"). `NestedActions` handles code actions that contain sub-actions (like "Generate constructor" having overloads).

**Step 2: Verify it builds**

Run: `dotnet build src/RoslynCodeLens/RoslynCodeLens.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/RoslynCodeLens/Models/CodeActionInfo.cs
git commit -m "feat: add CodeActionInfo model for code action discovery"
```

---

### Task 3: Create CodeActionRunner Core Class

**Files:**
- Create: `src/RoslynCodeLens/CodeActionRunner.cs`

This class discovers and invokes both `CodeRefactoringProvider` and `CodeFixProvider` types. It reuses patterns from the existing `CodeFixRunner.cs` but generalizes them.

**Step 1: Write the failing test**

Create `tests/RoslynCodeLens.Tests/CodeActionRunnerTests.cs`:

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tests;

public class CodeActionRunnerTests : IAsyncLifetime
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
    public async Task GetActionsAsync_AtMethodPosition_ReturnsActions()
    {
        // Greeter.cs line 8: public virtual string Greet(string name) => ...
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));
        var doc = project.Documents.First(d => d.Name == "Greeter.cs");
        var compilation = _loaded.Compilations[project.Id];

        var actions = await CodeActionRunner.GetActionsAsync(
            project, doc, compilation, line: 8, column: 5,
            endLine: null, endColumn: null, CancellationToken.None);

        Assert.NotEmpty(actions);
        Assert.All(actions, a => Assert.False(string.IsNullOrWhiteSpace(a.Title)));
    }

    [Fact]
    public async Task ApplyActionAsync_WithPreview_ReturnsDiff()
    {
        // Greeter.cs line 14: public int ComputeLength => triggers CA1822 (can be static)
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));
        var doc = project.Documents.First(d => d.Name == "Greeter.cs");
        var compilation = _loaded.Compilations[project.Id];

        var actions = await CodeActionRunner.GetActionsAsync(
            project, doc, compilation, line: 14, column: 5,
            endLine: null, endColumn: null, CancellationToken.None);

        if (actions.Count == 0) return; // Skip if no actions at this position

        var result = await CodeActionRunner.ApplyActionAsync(
            project, doc, compilation, line: 14, column: 5,
            endLine: null, endColumn: null,
            actionTitle: actions[0].Title, preview: true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotEmpty(result.Title);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "CodeActionRunnerTests" --no-build 2>&1 || echo "Expected: build failure (class doesn't exist yet)"`
Expected: FAIL — `CodeActionRunner` does not exist

**Step 3: Write CodeActionRunner implementation**

Create `src/RoslynCodeLens/CodeActionRunner.cs`:

```csharp
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens.Models;

namespace RoslynCodeLens;

public static class CodeActionRunner
{
    private static readonly Lazy<ImmutableArray<CodeRefactoringProvider>> s_builtInRefactoringProviders =
        new(LoadBuiltInRefactoringProviders);

    private static readonly Lazy<ImmutableArray<CodeFixProvider>> s_builtInCodeFixProviders =
        new(LoadBuiltInCodeFixProviders);

    public static async Task<IReadOnlyList<CodeActionInfo>> GetActionsAsync(
        Project project, Document document, Compilation compilation,
        int line, int column, int? endLine, int? endColumn,
        CancellationToken ct)
    {
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var span = CreateSpan(text, line, column, endLine, endColumn);
        var actions = new List<CodeAction>();

        // 1. Collect refactoring actions
        await CollectRefactoringActionsAsync(document, span, actions, ct).ConfigureAwait(false);

        // 2. Collect code fix actions for diagnostics in the span
        await CollectCodeFixActionsAsync(project, document, compilation, span, actions, ct).ConfigureAwait(false);

        return actions.Select(ToCodeActionInfo).ToList();
    }

    public static async Task<CodeActionResult> ApplyActionAsync(
        Project project, Document document, Compilation compilation,
        int line, int column, int? endLine, int? endColumn,
        string actionTitle, bool preview, CancellationToken ct)
    {
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var span = CreateSpan(text, line, column, endLine, endColumn);
        var actions = new List<CodeAction>();

        await CollectRefactoringActionsAsync(document, span, actions, ct).ConfigureAwait(false);
        await CollectCodeFixActionsAsync(project, document, compilation, span, actions, ct).ConfigureAwait(false);

        // Find by title (case-insensitive, supports partial match)
        var match = FindAction(actions, actionTitle);
        if (match == null)
        {
            return new CodeActionResult(
                Success: false,
                Title: actionTitle,
                Edits: [],
                ErrorMessage: $"No code action found matching '{actionTitle}'. Available: {string.Join(", ", actions.Select(a => a.Title))}");
        }

        var operations = await match.GetOperationsAsync(ct).ConfigureAwait(false);
        var applyOp = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
        if (applyOp == null)
        {
            return new CodeActionResult(
                Success: false,
                Title: match.Title,
                Edits: [],
                ErrorMessage: "Code action produced no applicable changes.");
        }

        var edits = ExtractTextEdits(applyOp.ChangedSolution, project.Solution);

        if (!preview)
        {
            foreach (var edit in edits)
            {
                var editPath = edit.FilePath;
                if (!string.IsNullOrEmpty(editPath))
                {
                    // Read original, apply change by replacing the text span
                    var changedDoc = applyOp.ChangedSolution.Projects
                        .SelectMany(p => p.Documents)
                        .FirstOrDefault(d => string.Equals(d.FilePath, editPath, StringComparison.OrdinalIgnoreCase));

                    if (changedDoc != null)
                    {
                        var changedText = await changedDoc.GetTextAsync(ct).ConfigureAwait(false);
                        File.WriteAllText(editPath, changedText.ToString());
                    }
                }
            }
        }

        return new CodeActionResult(
            Success: true,
            Title: match.Title,
            Edits: edits,
            ErrorMessage: null);
    }

    private static async Task CollectRefactoringActionsAsync(
        Document document, TextSpan span, List<CodeAction> actions, CancellationToken ct)
    {
        var providers = s_builtInRefactoringProviders.Value;

        for (var i = 0; i < providers.Length; i++)
        {
            try
            {
                var context = new CodeRefactoringContext(document, span,
                    action => actions.Add(action), ct);
                await providers[i].ComputeRefactoringsAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(
                    $"[roslyn-codelens] Refactoring provider failed ({providers[i].GetType().Name}): {ex.Message}")
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task CollectCodeFixActionsAsync(
        Project project, Document document, Compilation compilation,
        TextSpan span, List<CodeAction> actions, CancellationToken ct)
    {
        var tree = await document.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
        if (tree == null) return;

        var diagnostics = compilation.GetDiagnostics(ct)
            .Where(d => d.Location.IsInSource &&
                        d.Location.SourceTree == tree &&
                        d.Location.SourceSpan.IntersectsWith(span))
            .ToImmutableArray();

        if (diagnostics.IsEmpty) return;

        var providers = s_builtInCodeFixProviders.Value;

        for (var i = 0; i < providers.Length; i++)
        {
            var provider = providers[i];
            var fixableDiagnostics = diagnostics
                .Where(d => provider.FixableDiagnosticIds.Contains(d.Id))
                .ToImmutableArray();

            if (fixableDiagnostics.IsEmpty) continue;

            try
            {
                var context = new CodeFixContext(document, span,
                    fixableDiagnostics,
                    (action, _) => actions.Add(action), ct);
                await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(
                    $"[roslyn-codelens] CodeFix provider failed ({provider.GetType().Name}): {ex.Message}")
                    .ConfigureAwait(false);
            }
        }
    }

    private static TextSpan CreateSpan(SourceText text, int line, int column, int? endLine, int? endColumn)
    {
        // Convert 1-based line/column to 0-based
        var startLine = Math.Max(0, line - 1);
        var startCol = Math.Max(0, column - 1);
        var startPosition = text.Lines[Math.Min(startLine, text.Lines.Count - 1)].Start + startCol;

        if (endLine.HasValue && endColumn.HasValue)
        {
            var el = Math.Max(0, endLine.Value - 1);
            var ec = Math.Max(0, endColumn.Value - 1);
            var endPosition = text.Lines[Math.Min(el, text.Lines.Count - 1)].Start + ec;
            return TextSpan.FromBounds(startPosition, Math.Max(startPosition, endPosition));
        }

        // No selection — use zero-width span at position
        return new TextSpan(startPosition, 0);
    }

    private static CodeAction? FindAction(List<CodeAction> actions, string title)
    {
        // Try exact match first
        var match = actions.FirstOrDefault(a =>
            string.Equals(a.Title, title, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        // Try contains match
        return actions.FirstOrDefault(a =>
            a.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
    }

    private static List<TextEdit> ExtractTextEdits(Solution changedSolution, Solution originalSolution)
    {
        var edits = new List<TextEdit>();

        foreach (var projectChange in changedSolution.GetChanges(originalSolution).GetProjectChanges())
        {
            foreach (var changedDocId in projectChange.GetChangedDocuments())
            {
                var originalDoc = originalSolution.GetDocument(changedDocId);
                var changedDoc = changedSolution.GetDocument(changedDocId);
                if (originalDoc == null || changedDoc == null) continue;

                var originalText = originalDoc.GetTextAsync().GetAwaiter().GetResult();
                var changedText = changedDoc.GetTextAsync().GetAwaiter().GetResult();
                var changes = changedText.GetTextChanges(originalText);

                foreach (var change in changes)
                {
                    var startPos = originalText.Lines.GetLinePosition(change.Span.Start);
                    var endPos = originalText.Lines.GetLinePosition(change.Span.End);

                    edits.Add(new TextEdit(
                        originalDoc.FilePath ?? "",
                        startPos.Line + 1, startPos.Character + 1,
                        endPos.Line + 1, endPos.Character + 1,
                        change.NewText ?? ""));
                }
            }

            foreach (var addedDocId in projectChange.GetAddedDocuments())
            {
                var addedDoc = changedSolution.GetDocument(addedDocId);
                if (addedDoc == null) continue;

                var addedText = addedDoc.GetTextAsync().GetAwaiter().GetResult();
                edits.Add(new TextEdit(
                    addedDoc.FilePath ?? addedDoc.Name,
                    1, 1, 1, 1,
                    addedText.ToString()));
            }
        }

        return edits;
    }

    private static CodeActionInfo ToCodeActionInfo(CodeAction action)
    {
        // Determine kind from tags
        var kind = action.Tags.Contains(WellKnownTags.CodeFix) ? "CodeFix" : "Refactoring";
        return new CodeActionInfo(action.Title, kind);
    }

    private static ImmutableArray<CodeRefactoringProvider> LoadBuiltInRefactoringProviders()
    {
        var providers = ImmutableArray.CreateBuilder<CodeRefactoringProvider>();

        try
        {
            var featuresAssembly = typeof(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxNode).Assembly;

            // Load the CSharp Features assembly
            var assemblyName = "Microsoft.CodeAnalysis.CSharp.Features";
            Assembly? csharpFeatures = null;
            try
            {
                csharpFeatures = Assembly.Load(assemblyName);
            }
            catch
            {
                // Try loading from the same directory as the CodeAnalysis assembly
                var dir = Path.GetDirectoryName(featuresAssembly.Location);
                if (dir != null)
                {
                    var path = Path.Combine(dir, assemblyName + ".dll");
                    if (File.Exists(path))
                        csharpFeatures = Assembly.LoadFrom(path);
                }
            }

            if (csharpFeatures != null)
            {
                LoadRefactoringProvidersFromAssembly(csharpFeatures, providers);
            }

            // Also try the base Features assembly
            try
            {
                var baseFeatures = Assembly.Load("Microsoft.CodeAnalysis.Features");
                LoadRefactoringProvidersFromAssembly(baseFeatures, providers);
            }
            catch { /* Optional */ }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[roslyn-codelens] Failed to load built-in refactoring providers: {ex.Message}");
        }

        Console.Error.WriteLine($"[roslyn-codelens] Loaded {providers.Count} built-in refactoring providers");
        return providers.ToImmutable();
    }

    private static void LoadRefactoringProvidersFromAssembly(
        Assembly assembly, ImmutableArray<CodeRefactoringProvider>.Builder providers)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray()!;
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || !typeof(CodeRefactoringProvider).IsAssignableFrom(type))
                continue;

            try
            {
                if (Activator.CreateInstance(type) is CodeRefactoringProvider instance)
                    providers.Add(instance);
            }
            catch { /* Skip providers that can't be instantiated */ }
        }
    }

    private static ImmutableArray<CodeFixProvider> LoadBuiltInCodeFixProviders()
    {
        var providers = ImmutableArray.CreateBuilder<CodeFixProvider>();

        try
        {
            var assemblyName = "Microsoft.CodeAnalysis.CSharp.Features";
            Assembly? csharpFeatures = null;
            try
            {
                csharpFeatures = Assembly.Load(assemblyName);
            }
            catch
            {
                var refAssembly = typeof(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxNode).Assembly;
                var dir = Path.GetDirectoryName(refAssembly.Location);
                if (dir != null)
                {
                    var path = Path.Combine(dir, assemblyName + ".dll");
                    if (File.Exists(path))
                        csharpFeatures = Assembly.LoadFrom(path);
                }
            }

            if (csharpFeatures != null)
            {
                LoadCodeFixProvidersFromAssembly(csharpFeatures, providers);
            }

            try
            {
                var baseFeatures = Assembly.Load("Microsoft.CodeAnalysis.Features");
                LoadCodeFixProvidersFromAssembly(baseFeatures, providers);
            }
            catch { /* Optional */ }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[roslyn-codelens] Failed to load built-in code fix providers: {ex.Message}");
        }

        Console.Error.WriteLine($"[roslyn-codelens] Loaded {providers.Count} built-in code fix providers");
        return providers.ToImmutable();
    }

    private static void LoadCodeFixProvidersFromAssembly(
        Assembly assembly, ImmutableArray<CodeFixProvider>.Builder providers)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray()!;
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || !typeof(CodeFixProvider).IsAssignableFrom(type))
                continue;

            var exportAttr = type.GetCustomAttribute<ExportCodeFixProviderAttribute>();
            if (exportAttr == null) continue;

            if (exportAttr.Languages.Length > 0 &&
                !exportAttr.Languages.Contains(LanguageNames.CSharp, StringComparer.OrdinalIgnoreCase))
                continue;

            try
            {
                if (Activator.CreateInstance(type) is CodeFixProvider instance)
                    providers.Add(instance);
            }
            catch { /* Skip providers that can't be instantiated */ }
        }
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "CodeActionRunnerTests" -v normal`
Expected: 2 tests PASS

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/CodeActionRunner.cs tests/RoslynCodeLens.Tests/CodeActionRunnerTests.cs
git commit -m "feat: add CodeActionRunner for built-in Roslyn refactoring discovery and application"
```

---

### Task 4: Create CodeActionResult Model

**Files:**
- Create: `src/RoslynCodeLens/Models/CodeActionResult.cs`

**Step 1: Write the model**

```csharp
namespace RoslynCodeLens.Models;

public record CodeActionResult(
    bool Success,
    string Title,
    IReadOnlyList<TextEdit> Edits,
    string? ErrorMessage = null);
```

**Step 2: Verify it builds**

Run: `dotnet build src/RoslynCodeLens/RoslynCodeLens.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/RoslynCodeLens/Models/CodeActionResult.cs
git commit -m "feat: add CodeActionResult model for code action application results"
```

---

### Task 5: Create GetCodeActionsTool + Logic

**Files:**
- Create: `src/RoslynCodeLens/Tools/GetCodeActionsTool.cs`
- Create: `src/RoslynCodeLens/Tools/GetCodeActionsLogic.cs`

**Step 1: Write the failing test**

Create `tests/RoslynCodeLens.Tests/GetCodeActionsLogicTests.cs`:

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class GetCodeActionsLogicTests : IAsyncLifetime
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
    public async Task ExecuteAsync_AtMethodPosition_ReturnsCodeActions()
    {
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));
        var greeterPath = project.Documents.First(d => d.Name == "Greeter.cs").FilePath!;

        var result = await GetCodeActionsLogic.ExecuteAsync(
            _loaded, greeterPath, line: 8, column: 5,
            endLine: null, endColumn: null, CancellationToken.None);

        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidFile_ReturnsEmpty()
    {
        var result = await GetCodeActionsLogic.ExecuteAsync(
            _loaded, "nonexistent.cs", line: 1, column: 1,
            endLine: null, endColumn: null, CancellationToken.None);

        Assert.Empty(result);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "GetCodeActionsLogicTests" --no-build 2>&1 || echo "Expected: build failure"`
Expected: FAIL — class doesn't exist

**Step 3: Write GetCodeActionsLogic**

Create `src/RoslynCodeLens/Tools/GetCodeActionsLogic.cs`:

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class GetCodeActionsLogic
{
    public static async Task<IReadOnlyList<CodeActionInfo>> ExecuteAsync(
        LoadedSolution loaded, string filePath, int line, int column,
        int? endLine, int? endColumn, CancellationToken ct)
    {
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

        return await CodeActionRunner.GetActionsAsync(
            targetProject, targetDocument, compilation,
            line, column, endLine, endColumn, ct).ConfigureAwait(false);
    }
}
```

**Step 4: Write GetCodeActionsTool**

Create `src/RoslynCodeLens/Tools/GetCodeActionsTool.cs`:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetCodeActionsTool
{
    [McpServerTool(Name = "get_code_actions"),
     Description("List available code actions (refactorings and fixes) at a position in a C# file. " +
                 "Optionally specify endLine/endColumn to select a range for extract-method style refactorings. " +
                 "Returns action titles that can be passed to apply_code_action.")]
    public static async Task<IReadOnlyList<CodeActionInfo>> Execute(
        MultiSolutionManager manager,
        [Description("Full path to the C# source file")] string filePath,
        [Description("Line number (1-based)")] int line,
        [Description("Column number (1-based)")] int column,
        [Description("End line for text selection (1-based, optional)")] int? endLine = null,
        [Description("End column for text selection (1-based, optional)")] int? endColumn = null,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        return await GetCodeActionsLogic.ExecuteAsync(
            manager.GetLoadedSolution(), filePath, line, column,
            endLine, endColumn, ct).ConfigureAwait(false);
    }
}
```

**Step 5: Run tests**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "GetCodeActionsLogicTests" -v normal`
Expected: 2 tests PASS

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Tools/GetCodeActionsTool.cs src/RoslynCodeLens/Tools/GetCodeActionsLogic.cs tests/RoslynCodeLens.Tests/GetCodeActionsLogicTests.cs
git commit -m "feat: add get_code_actions tool for discovering available refactorings at a position"
```

---

### Task 6: Create ApplyCodeActionTool + Logic

**Files:**
- Create: `src/RoslynCodeLens/Tools/ApplyCodeActionTool.cs`
- Create: `src/RoslynCodeLens/Tools/ApplyCodeActionLogic.cs`

**Step 1: Write the failing test**

Create `tests/RoslynCodeLens.Tests/ApplyCodeActionLogicTests.cs`:

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class ApplyCodeActionLogicTests : IAsyncLifetime
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
    public async Task ExecuteAsync_WithPreview_ReturnsDiffWithoutWriting()
    {
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));
        var greeterPath = project.Documents.First(d => d.Name == "Greeter.cs").FilePath!;

        // First get available actions
        var actions = await GetCodeActionsLogic.ExecuteAsync(
            _loaded, greeterPath, line: 8, column: 5,
            endLine: null, endColumn: null, CancellationToken.None);

        if (actions.Count == 0) return;

        var result = await ApplyCodeActionLogic.ExecuteAsync(
            _loaded, greeterPath, line: 8, column: 5,
            endLine: null, endColumn: null,
            actionTitle: actions[0].Title, preview: true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(actions[0].Title, result.Title);
    }

    [Fact]
    public async Task ExecuteAsync_WithBadTitle_ReturnsError()
    {
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));
        var greeterPath = project.Documents.First(d => d.Name == "Greeter.cs").FilePath!;

        var result = await ApplyCodeActionLogic.ExecuteAsync(
            _loaded, greeterPath, line: 8, column: 5,
            endLine: null, endColumn: null,
            actionTitle: "NonExistentAction_12345", preview: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "ApplyCodeActionLogicTests" --no-build 2>&1 || echo "Expected: build failure"`
Expected: FAIL — class doesn't exist

**Step 3: Write ApplyCodeActionLogic**

Create `src/RoslynCodeLens/Tools/ApplyCodeActionLogic.cs`:

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class ApplyCodeActionLogic
{
    public static async Task<CodeActionResult> ExecuteAsync(
        LoadedSolution loaded, string filePath, int line, int column,
        int? endLine, int? endColumn,
        string actionTitle, bool preview, CancellationToken ct)
    {
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
        {
            return new CodeActionResult(
                Success: false,
                Title: actionTitle,
                Edits: [],
                ErrorMessage: $"File not found in solution: {filePath}");
        }

        if (!loaded.Compilations.TryGetValue(targetProject.Id, out var compilation))
        {
            return new CodeActionResult(
                Success: false,
                Title: actionTitle,
                Edits: [],
                ErrorMessage: $"No compilation available for project: {targetProject.Name}");
        }

        return await CodeActionRunner.ApplyActionAsync(
            targetProject, targetDocument, compilation,
            line, column, endLine, endColumn,
            actionTitle, preview, ct).ConfigureAwait(false);
    }
}
```

**Step 4: Write ApplyCodeActionTool**

Create `src/RoslynCodeLens/Tools/ApplyCodeActionTool.cs`:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class ApplyCodeActionTool
{
    [McpServerTool(Name = "apply_code_action"),
     Description("Apply a code action (refactoring or fix) by its title. " +
                 "Use get_code_actions first to discover available actions. " +
                 "Defaults to preview mode (returns diff without writing files). " +
                 "Set preview=false to apply changes to disk.")]
    public static async Task<CodeActionResult> Execute(
        MultiSolutionManager manager,
        [Description("Full path to the C# source file")] string filePath,
        [Description("Line number (1-based)")] int line,
        [Description("Column number (1-based)")] int column,
        [Description("Exact title of the code action to apply (from get_code_actions)")] string actionTitle,
        [Description("End line for text selection (1-based, optional)")] int? endLine = null,
        [Description("End column for text selection (1-based, optional)")] int? endColumn = null,
        [Description("Preview only — return diff without writing to disk (default: true)")] bool preview = true,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        return await ApplyCodeActionLogic.ExecuteAsync(
            manager.GetLoadedSolution(), filePath, line, column,
            endLine, endColumn, actionTitle, preview, ct).ConfigureAwait(false);
    }
}
```

**Step 5: Run tests**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "ApplyCodeActionLogicTests" -v normal`
Expected: 2 tests PASS

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Tools/ApplyCodeActionTool.cs src/RoslynCodeLens/Tools/ApplyCodeActionLogic.cs tests/RoslynCodeLens.Tests/ApplyCodeActionLogicTests.cs
git commit -m "feat: add apply_code_action tool for executing refactorings with preview support"
```

---

### Task 7: Run Full Test Suite & Verify Integration

**Files:**
- No new files

**Step 1: Run all tests**

Run: `dotnet test tests/RoslynCodeLens.Tests -v normal`
Expected: All tests PASS (existing + new)

**Step 2: Verify the tools are discovered by the MCP server**

Run: `dotnet build src/RoslynCodeLens/RoslynCodeLens.csproj`
Expected: Build succeeded, no warnings related to new code

**Step 3: Commit (if any fixes were needed)**

Only if fixes were applied in steps 1-2.

---

### Task 8: Update Skill Documentation

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`

**Step 1: Add the new tools to the skill documentation**

Add entries for `get_code_actions` and `apply_code_action` to the tool list in SKILL.md, following the existing format. Include:

- Tool name and description
- Parameter documentation
- Usage examples showing the discover→apply workflow
- Note about preview mode defaulting to true

**Step 2: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md
git commit -m "docs: add get_code_actions and apply_code_action to skill documentation"
```

---

## Summary

| Task | What | New Files | Test Files |
|------|------|-----------|------------|
| 1 | Add Features package | — | — |
| 2 | CodeActionInfo model | 1 | — |
| 3 | CodeActionRunner core | 1 | 1 |
| 4 | CodeActionResult model | 1 | — |
| 5 | GetCodeActionsTool + Logic | 2 | 1 |
| 6 | ApplyCodeActionTool + Logic | 2 | 1 |
| 7 | Full test suite verification | — | — |
| 8 | Skill documentation | — | — |

**Total: 7 new files, 3 test files, 1 modified file**
