# `find_breaking_changes` Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a new MCP tool `find_breaking_changes` that diffs the current solution's public API surface against a baseline (JSON snapshot from `get_public_api_surface` or a `.dll` file). Reports five change kinds with hardcoded severity (Breaking/NonBreaking).

**Architecture:** Linear set diff on FQNs. Reuses `GetPublicApiSurfaceLogic` to extract the current surface. Loads the baseline from JSON or via Roslyn `MetadataReference` for a DLL. Requires extracting `PublicApiSurfaceExtractor` from `GetPublicApiSurfaceLogic` first so both source-walk and assembly-walk paths share the type/member enumeration.

**Tech Stack:** Roslyn (`Microsoft.CodeAnalysis`, `Microsoft.CodeAnalysis.CSharp`), `System.Text.Json` for snapshot loading, xUnit, BenchmarkDotNet, ModelContextProtocol.Server.

**Reference design:** `docs/plans/2026-04-29-find-breaking-changes-design.md`

**Patterns to mirror (read these before starting):**
- Tool wrapper: `src/RoslynCodeLens/Tools/GetPublicApiSurfaceTool.cs`
- Logic class: `src/RoslynCodeLens/Tools/GetPublicApiSurfaceLogic.cs`
- Test pattern: `tests/RoslynCodeLens.Tests/Tools/GetPublicApiSurfaceToolTests.cs`
- MCP auto-registration: `src/RoslynCodeLens/Program.cs:35` uses `WithToolsFromAssembly()` — no `Program.cs` edit needed

---

## Task 1: Extract `PublicApiSurfaceExtractor` shared helper

`GetPublicApiSurfaceLogic` currently contains both the iteration loop AND all the type/member enumeration helpers. Extract the helpers + the iteration of one assembly into a shared `PublicApiSurfaceExtractor`. Both source-walk (current path) and assembly-walk (new baseline path in `find_breaking_changes`) need it.

This is a behavior-preserving refactor: existing 30 `GetPublicApiSurfaceToolTests` must still pass.

**Files:**
- Create: `src/RoslynCodeLens/Analysis/PublicApiSurfaceExtractor.cs`
- Modify: `src/RoslynCodeLens/Tools/GetPublicApiSurfaceLogic.cs`

**Step 1: Create `PublicApiSurfaceExtractor.cs`**

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Analysis;

/// <summary>
/// Walks an <see cref="IAssemblySymbol"/> and yields every public-or-reachable-protected entry
/// (type and member) for the API surface.
/// </summary>
public static class PublicApiSurfaceExtractor
{
    private static readonly SymbolDisplayFormat ApiMemberFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
                     | SymbolDisplayMemberOptions.IncludeParameters
                     | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <param name="assembly">Assembly to walk.</param>
    /// <param name="projectName">Project name attached to each entry.</param>
    /// <param name="requireSourceLocation">
    /// True for source compilations (skip metadata-only members and members in generated files).
    /// False for baseline DLL extraction (include everything in the assembly).
    /// </param>
    public static IReadOnlyList<PublicApiEntry> Extract(
        IAssemblySymbol assembly,
        string projectName,
        bool requireSourceLocation)
    {
        var entries = new List<PublicApiEntry>();

        foreach (var type in EnumerateTypes(assembly.GlobalNamespace))
        {
            if (!IsApiVisibleType(type, requireSourceLocation)) continue;

            entries.Add(BuildTypeEntry(type, projectName, requireSourceLocation));

            var protectedReachable = type.TypeKind == TypeKind.Class && !type.IsSealed;
            foreach (var member in type.GetMembers())
            {
                if (member is INamedTypeSymbol) continue;
                if (ShouldSkipMember(member)) continue;

                var apiAcc = ClassifyMemberAccessibility(member, protectedReachable);
                if (apiAcc is null) continue;

                if (requireSourceLocation)
                {
                    if (!HasInSourceLocation(member)) continue;
                    if (IsInGeneratedFile(member)) continue;
                }

                var entry = BuildMemberEntry(member, apiAcc.Value, projectName, requireSourceLocation);
                if (entry is not null)
                    entries.Add(entry);
            }
        }

        return entries;
    }

    private static bool IsApiVisibleType(INamedTypeSymbol type, bool requireSourceLocation)
    {
        if (type.IsImplicitlyDeclared) return false;

        if (requireSourceLocation)
        {
            if (!HasInSourceLocation(type)) return false;
            if (IsInGeneratedFile(type)) return false;
        }

        // Every type in the containing-type chain must also be Public.
        // A `public class Nested` inside an `internal class Outer` is NOT externally reachable.
        for (var t = type; t is not null; t = t.ContainingType)
        {
            if (t.DeclaredAccessibility != Accessibility.Public) return false;
        }

        return true;
    }

    private static bool HasInSourceLocation(ISymbol symbol)
        => symbol.Locations.Any(l => l.IsInSource);

    private static bool ShouldSkipMember(ISymbol member)
    {
        // Positional record properties are implicitly declared (synthesized from primary constructor
        // parameters), but ARE part of the public API surface — keep them.
        if (member is IPropertySymbol prop
            && prop.ContainingType is { IsRecord: true }
            && !string.Equals(prop.Name, "EqualityContract", StringComparison.Ordinal))
        {
            return false;
        }

        // Record primary constructors are also implicit but ARE part of the API. Detect them by
        // checking that the first parameter type is NOT the containing type itself (which would
        // mark the synthesized copy constructor we want to drop).
        if (member is IMethodSymbol method
            && method.MethodKind == MethodKind.Constructor
            && method.ContainingType is { IsRecord: true }
            && method.Parameters.Length > 0
            && !SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, method.ContainingType))
        {
            return false;
        }

        return member.IsImplicitlyDeclared;
    }

    private static bool IsInGeneratedFile(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location?.SourceTree is null) return false;
        return GeneratedCodeDetector.IsGenerated(location.SourceTree);
    }

    private static PublicApiAccessibility? ClassifyMemberAccessibility(ISymbol member, bool protectedReachable)
    {
        switch (member.DeclaredAccessibility)
        {
            case Accessibility.Public:
                return PublicApiAccessibility.Public;
            case Accessibility.Protected:
            case Accessibility.ProtectedOrInternal:
                return protectedReachable ? PublicApiAccessibility.Protected : null;
            default:
                return null;
        }
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

    private static PublicApiEntry BuildTypeEntry(INamedTypeSymbol type, string projectName, bool requireSourceLocation)
    {
        var (filePath, line) = LocationFor(type, requireSourceLocation);

        return new PublicApiEntry(
            Kind: TypeKindToApiKind(type),
            Name: FullyQualified(type),
            Accessibility: PublicApiAccessibility.Public,
            Project: projectName,
            FilePath: filePath,
            Line: line);
    }

    private static PublicApiKind TypeKindToApiKind(INamedTypeSymbol type)
    {
        if (type.IsRecord)
        {
            return type.TypeKind == TypeKind.Struct ? PublicApiKind.RecordStruct : PublicApiKind.Record;
        }

        return type.TypeKind switch
        {
            TypeKind.Class => PublicApiKind.Class,
            TypeKind.Struct => PublicApiKind.Struct,
            TypeKind.Interface => PublicApiKind.Interface,
            TypeKind.Enum => PublicApiKind.Enum,
            TypeKind.Delegate => PublicApiKind.Delegate,
            _ => PublicApiKind.Class
        };
    }

    private static PublicApiEntry? BuildMemberEntry(ISymbol member, PublicApiAccessibility apiAcc, string projectName, bool requireSourceLocation)
    {
        var (kind, name) = MemberKindAndName(member);
        if (kind is null) return null;

        var (filePath, line) = LocationFor(member, requireSourceLocation);
        return new PublicApiEntry(
            Kind: kind.Value,
            Name: name,
            Accessibility: apiAcc,
            Project: projectName,
            FilePath: filePath,
            Line: line);
    }

    private static (string FilePath, int Line) LocationFor(ISymbol symbol, bool requireSourceLocation)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is not null)
        {
            var lineSpan = location.GetLineSpan();
            return (lineSpan.Path, lineSpan.StartLinePosition.Line + 1);
        }

        if (requireSourceLocation)
        {
            // Should not happen — IsApiVisibleType already filtered metadata-only types away.
            return (string.Empty, 0);
        }

        // Metadata symbol (baseline DLL path) — no source location available.
        return (string.Empty, 0);
    }

    private static (PublicApiKind? Kind, string Name) MemberKindAndName(ISymbol member)
    {
        var memberName = MemberDisplayName(member);

        switch (member)
        {
            case IMethodSymbol method:
                return method.MethodKind switch
                {
                    MethodKind.Constructor => (PublicApiKind.Constructor, memberName),
                    MethodKind.UserDefinedOperator => (PublicApiKind.Operator, memberName),
                    MethodKind.Conversion => (PublicApiKind.Operator, memberName),
                    MethodKind.Ordinary => (PublicApiKind.Method, memberName),
                    MethodKind.PropertyGet or MethodKind.PropertySet
                        or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise
                        or MethodKind.Destructor
                        or MethodKind.StaticConstructor => (null, memberName),
                    _ => (null, memberName)
                };

            case IPropertySymbol property:
                return property.IsIndexer
                    ? (PublicApiKind.Indexer, memberName)
                    : (PublicApiKind.Property, memberName);

            case IFieldSymbol:
                return (PublicApiKind.Field, memberName);

            case IEventSymbol:
                return (PublicApiKind.Event, memberName);

            default:
                return (null, memberName);
        }
    }

    private static string MemberDisplayName(ISymbol member)
        => member.ToDisplayString(ApiMemberFormat);

    private static string FullyQualified(ISymbol symbol)
        => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal);
}
```

Note: `LocationFor` is new — it returns blank `FilePath`/`Line` for symbols without source locations (i.e., metadata members from a baseline DLL). That's acceptable since baseline-DLL entries are used for diff identity, not for navigation.

**Step 2: Update `GetPublicApiSurfaceLogic.cs`** — replace the existing implementation with a thin wrapper around the extractor:

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class GetPublicApiSurfaceLogic
{
    public static GetPublicApiSurfaceResult Execute(LoadedSolution loaded, SymbolResolver source)
    {
        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);
        var entries = new List<PublicApiEntry>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            if (testProjectIds.Contains(projectId)) continue;

            var projectName = source.GetProjectName(projectId);
            entries.AddRange(PublicApiSurfaceExtractor.Extract(
                compilation.Assembly,
                projectName,
                requireSourceLocation: true));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var byKind = entries
            .GroupBy(e => e.Kind.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byProject = entries
            .GroupBy(e => e.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byAccessibility = entries
            .GroupBy(e => e.Accessibility.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var summary = new PublicApiSummary(
            TotalEntries: entries.Count,
            ByKind: byKind,
            ByProject: byProject,
            ByAccessibility: byAccessibility);

        return new GetPublicApiSurfaceResult(summary, entries);
    }
}
```

Drop all the private helpers — they're now in the extractor.

**Step 3: Build to verify**

```bash
dotnet build
```

Expected: 0 errors.

**Step 4: Run existing tests**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetPublicApiSurfaceToolTests" -v normal
```

Expected: all 30 tests pass (was 30 before this task).

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Analysis/PublicApiSurfaceExtractor.cs \
  src/RoslynCodeLens/Tools/GetPublicApiSurfaceLogic.cs
git commit -m "refactor: extract PublicApiSurfaceExtractor shared helper"
```

---

## Task 2: Models for find_breaking_changes output

Five files: two enums + three records.

**Files:**
- Create: `src/RoslynCodeLens/Models/BreakingChangeKind.cs`
- Create: `src/RoslynCodeLens/Models/BreakingChangeSeverity.cs`
- Create: `src/RoslynCodeLens/Models/BreakingChange.cs`
- Create: `src/RoslynCodeLens/Models/BreakingChangesSummary.cs`
- Create: `src/RoslynCodeLens/Models/FindBreakingChangesResult.cs`

**Step 1: `BreakingChangeKind.cs`**

```csharp
namespace RoslynCodeLens.Models;

public enum BreakingChangeKind
{
    Removed,
    Added,
    KindChanged,
    AccessibilityNarrowed,
    AccessibilityWidened
}
```

**Step 2: `BreakingChangeSeverity.cs`**

```csharp
namespace RoslynCodeLens.Models;

public enum BreakingChangeSeverity
{
    Breaking,
    NonBreaking
}
```

**Step 3: `BreakingChange.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record BreakingChange(
    BreakingChangeKind Kind,
    BreakingChangeSeverity Severity,
    string Name,
    PublicApiKind EntityKind,
    string Project,
    string FilePath,
    int Line,
    string Details);
```

**Step 4: `BreakingChangesSummary.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record BreakingChangesSummary(
    int TotalChanges,
    IReadOnlyDictionary<string, int> ByKind,
    IReadOnlyDictionary<string, int> BySeverity);
```

**Step 5: `FindBreakingChangesResult.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record FindBreakingChangesResult(
    BreakingChangesSummary Summary,
    IReadOnlyList<BreakingChange> Changes);
```

**Step 6: Build**

```bash
dotnet build src/RoslynCodeLens
```

Expected: 0 errors.

**Step 7: Commit**

```bash
git add src/RoslynCodeLens/Models/BreakingChangeKind.cs \
  src/RoslynCodeLens/Models/BreakingChangeSeverity.cs \
  src/RoslynCodeLens/Models/BreakingChange.cs \
  src/RoslynCodeLens/Models/BreakingChangesSummary.cs \
  src/RoslynCodeLens/Models/FindBreakingChangesResult.cs
git commit -m "feat: add models for find_breaking_changes output"
```

---

## Task 3: `FindBreakingChangesLogic` + comprehensive tests

The diff engine. Set-difference on FQN, classification, sort, summary, plus baseline loading from JSON or DLL.

**Files:**
- Create: `src/RoslynCodeLens/Tools/FindBreakingChangesLogic.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/FindBreakingChangesToolTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/RoslynCodeLens.Tests/Tools/FindBreakingChangesToolTests.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindBreakingChangesToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;
    private IReadOnlyList<PublicApiEntry> _currentSurface = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _resolver = new SymbolResolver(_loaded);
        _currentSurface = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver).Entries;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Diff_NoChanges_ReturnsEmpty()
    {
        // Identity: baseline equals current → zero changes.
        var result = FindBreakingChangesLogic.Diff(_currentSurface, _currentSurface);

        Assert.Empty(result.Changes);
        Assert.Equal(0, result.Summary.TotalChanges);
    }

    [Fact]
    public void Diff_SymbolRemoved_ReportedAsRemovedBreaking()
    {
        var fakeRemoved = new PublicApiEntry(
            PublicApiKind.Method, "Fake.Removed.Method()", PublicApiAccessibility.Public,
            "FakeProj", "Fake.cs", 1);
        var baseline = _currentSurface.Concat(new[] { fakeRemoved }).ToList();

        var result = FindBreakingChangesLogic.Diff(baseline, _currentSurface);

        var removed = Assert.Single(result.Changes, c => c.Name == "Fake.Removed.Method()");
        Assert.Equal(BreakingChangeKind.Removed, removed.Kind);
        Assert.Equal(BreakingChangeSeverity.Breaking, removed.Severity);
    }

    [Fact]
    public void Diff_SymbolAdded_ReportedAsAddedNonBreaking()
    {
        // Drop one entry from baseline so it appears as Added in current.
        var dropped = _currentSurface.First(e => e.Name == "TestLib.Greeter");
        var baseline = _currentSurface.Where(e => e.Name != dropped.Name).ToList();

        var result = FindBreakingChangesLogic.Diff(baseline, _currentSurface);

        var added = Assert.Single(result.Changes, c => c.Name == dropped.Name);
        Assert.Equal(BreakingChangeKind.Added, added.Kind);
        Assert.Equal(BreakingChangeSeverity.NonBreaking, added.Severity);
    }

    [Fact]
    public void Diff_KindChanged_ReportedAsKindChangedBreaking()
    {
        // Take an existing Class and pretend the baseline had it as a Struct.
        var greeter = _currentSurface.First(e => e.Name == "TestLib.Greeter");
        var baselineMutated = _currentSurface
            .Where(e => e.Name != greeter.Name)
            .Concat(new[] { greeter with { Kind = PublicApiKind.Struct } })
            .ToList();

        var result = FindBreakingChangesLogic.Diff(baselineMutated, _currentSurface);

        var change = Assert.Single(result.Changes, c => c.Name == "TestLib.Greeter");
        Assert.Equal(BreakingChangeKind.KindChanged, change.Kind);
        Assert.Equal(BreakingChangeSeverity.Breaking, change.Severity);
    }

    [Fact]
    public void Diff_AccessibilityNarrowed_Breaking()
    {
        // Pick a public method and pretend the baseline had it as Public; current is Protected
        // → narrowed.
        // Use AbstractProcessor.Process — currently Protected. Pretend baseline had it as Public.
        var processProtected = _currentSurface.First(e =>
            e.Name.Contains("AbstractProcessor.Process", StringComparison.Ordinal));
        var baselineMutated = _currentSurface
            .Where(e => e.Name != processProtected.Name)
            .Concat(new[] { processProtected with { Accessibility = PublicApiAccessibility.Public } })
            .ToList();

        var result = FindBreakingChangesLogic.Diff(baselineMutated, _currentSurface);

        var change = Assert.Single(result.Changes, c => c.Name == processProtected.Name);
        Assert.Equal(BreakingChangeKind.AccessibilityNarrowed, change.Kind);
        Assert.Equal(BreakingChangeSeverity.Breaking, change.Severity);
    }

    [Fact]
    public void Diff_AccessibilityWidened_NonBreaking()
    {
        // Pick a public method and pretend the baseline had it as Protected.
        var greet = _currentSurface.First(e => e.Name == "TestLib.Greeter.Greet(string)");
        var baselineMutated = _currentSurface
            .Where(e => e.Name != greet.Name)
            .Concat(new[] { greet with { Accessibility = PublicApiAccessibility.Protected } })
            .ToList();

        var result = FindBreakingChangesLogic.Diff(baselineMutated, _currentSurface);

        var change = Assert.Single(result.Changes, c => c.Name == greet.Name);
        Assert.Equal(BreakingChangeKind.AccessibilityWidened, change.Kind);
        Assert.Equal(BreakingChangeSeverity.NonBreaking, change.Severity);
    }

    [Fact]
    public void Severity_BreakingBeforeNonBreaking_InSort()
    {
        var fake1 = new PublicApiEntry(
            PublicApiKind.Method, "Fake.Removed1()", PublicApiAccessibility.Public, "P", "f", 1);
        var fake2 = new PublicApiEntry(
            PublicApiKind.Method, "Fake.Removed2()", PublicApiAccessibility.Public, "P", "f", 1);
        var baseline = _currentSurface.Concat(new[] { fake1, fake2 }).ToList();

        var result = FindBreakingChangesLogic.Diff(baseline, _currentSurface);

        // All Breaking entries should come first.
        var firstNonBreaking = result.Changes
            .Select((c, i) => new { c, i })
            .FirstOrDefault(x => x.c.Severity == BreakingChangeSeverity.NonBreaking);
        if (firstNonBreaking is not null)
        {
            for (int i = firstNonBreaking.i; i < result.Changes.Count; i++)
                Assert.Equal(BreakingChangeSeverity.NonBreaking, result.Changes[i].Severity);
        }
    }

    [Fact]
    public void Within_Severity_NameOrdinal()
    {
        var fakeA = new PublicApiEntry(
            PublicApiKind.Method, "AAAA.Removed()", PublicApiAccessibility.Public, "P", "f", 1);
        var fakeZ = new PublicApiEntry(
            PublicApiKind.Method, "ZZZZ.Removed()", PublicApiAccessibility.Public, "P", "f", 1);
        var baseline = _currentSurface.Concat(new[] { fakeZ, fakeA }).ToList();

        var result = FindBreakingChangesLogic.Diff(baseline, _currentSurface);

        for (int i = 1; i < result.Changes.Count; i++)
        {
            var prev = result.Changes[i - 1];
            var curr = result.Changes[i];
            if (prev.Severity == curr.Severity)
            {
                Assert.True(string.CompareOrdinal(prev.Name, curr.Name) <= 0,
                    $"Sort violation at {i}: '{prev.Name}' > '{curr.Name}'");
            }
        }
    }

    [Fact]
    public void Summary_TotalMatchesListLength()
    {
        var fake = new PublicApiEntry(
            PublicApiKind.Method, "Fake.Removed()", PublicApiAccessibility.Public, "P", "f", 1);
        var baseline = _currentSurface.Concat(new[] { fake }).ToList();

        var result = FindBreakingChangesLogic.Diff(baseline, _currentSurface);

        Assert.Equal(result.Changes.Count, result.Summary.TotalChanges);
    }

    [Fact]
    public void Summary_ByKindAndBySeverityCountsAreCorrect()
    {
        var fake = new PublicApiEntry(
            PublicApiKind.Method, "Fake.Removed()", PublicApiAccessibility.Public, "P", "f", 1);
        var baseline = _currentSurface.Concat(new[] { fake }).ToList();

        var result = FindBreakingChangesLogic.Diff(baseline, _currentSurface);

        foreach (var (kindName, count) in result.Summary.ByKind)
        {
            var actual = result.Changes.Count(c => c.Kind.ToString() == kindName);
            Assert.Equal(actual, count);
        }
        foreach (var (severityName, count) in result.Summary.BySeverity)
        {
            var actual = result.Changes.Count(c => c.Severity.ToString() == severityName);
            Assert.Equal(actual, count);
        }
    }

    [Fact]
    public void Json_Baseline_IdentityRoundtrip()
    {
        // Write the current surface to JSON, load it back as the baseline, diff.
        // Should produce zero changes.
        var path = WriteBaselineJson(_currentSurface);
        try
        {
            var result = FindBreakingChangesLogic.Execute(_loaded, _resolver, path);
            Assert.Empty(result.Changes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Assembly_Baseline_RoundtripsCleanly()
    {
        // Compile a tiny in-memory assembly to disk, point the tool at it, expect Removed entries
        // for the fixture's symbols (since the synthetic baseline doesn't have them) and Added
        // entries for the synthetic baseline's symbols (since current doesn't have them).
        var src = """
            namespace BaselineProbe
            {
                public class Foo
                {
                    public void Bar() {}
                }
            }
            """;
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "baseline-test",
            new[] { CSharpSyntaxTree.ParseText(src) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var dllPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dll");
        try
        {
            using (var fs = File.Create(dllPath))
            {
                var emitResult = compilation.Emit(fs);
                Assert.True(emitResult.Success, $"Emit failed: {string.Join(", ", emitResult.Diagnostics)}");
            }

            var result = FindBreakingChangesLogic.Execute(_loaded, _resolver, dllPath);

            // Synthetic baseline has BaselineProbe.Foo and BaselineProbe.Foo.Bar() — both should
            // appear as Removed (in baseline, not in current).
            Assert.Contains(result.Changes, c =>
                c.Name == "BaselineProbe.Foo" && c.Kind == BreakingChangeKind.Removed);
            Assert.Contains(result.Changes, c =>
                c.Name.Contains("BaselineProbe.Foo.Bar", StringComparison.Ordinal) &&
                c.Kind == BreakingChangeKind.Removed);
        }
        finally
        {
            if (File.Exists(dllPath)) File.Delete(dllPath);
        }
    }

    [Fact]
    public void MissingBaselineFile_ThrowsFileNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        Assert.Throws<FileNotFoundException>(() =>
            FindBreakingChangesLogic.Execute(_loaded, _resolver, path));
    }

    [Fact]
    public void MalformedBaselineJson_ThrowsInvalidOperation()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, "{ this is not valid json");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                FindBreakingChangesLogic.Execute(_loaded, _resolver, path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnknownExtension_ThrowsInvalidOperation()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        File.WriteAllText(path, "<root/>");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                FindBreakingChangesLogic.Execute(_loaded, _resolver, path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteBaselineJson(IReadOnlyList<PublicApiEntry> entries)
    {
        var byKind = entries
            .GroupBy(e => e.Kind.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byProject = entries
            .GroupBy(e => e.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byAccessibility = entries
            .GroupBy(e => e.Accessibility.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var summary = new PublicApiSummary(entries.Count, byKind, byProject, byAccessibility);
        var result = new GetPublicApiSurfaceResult(summary, entries);

        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        opts.Converters.Add(new JsonStringEnumConverter());

        var json = JsonSerializer.Serialize(result, opts);
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, json);
        return path;
    }
}
```

**Step 2: Run to verify they fail (compile errors)**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindBreakingChangesToolTests" -v normal
```

Expected: compile error — `FindBreakingChangesLogic` doesn't exist.

**Step 3: Create `FindBreakingChangesLogic.cs`**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class FindBreakingChangesLogic
{
    private static readonly JsonSerializerOptions JsonOpts = CreateJsonOptions();

    public static FindBreakingChangesResult Execute(LoadedSolution loaded, SymbolResolver source, string baselinePath)
    {
        var baseline = LoadBaseline(baselinePath);
        var current = GetPublicApiSurfaceLogic.Execute(loaded, source).Entries;
        return Diff(baseline, current);
    }

    /// <summary>
    /// Pure diff entry point — exposed internally so tests can pass in-memory baselines
    /// without writing and reloading JSON.
    /// </summary>
    internal static FindBreakingChangesResult Diff(
        IReadOnlyList<PublicApiEntry> baseline,
        IReadOnlyList<PublicApiEntry> current)
    {
        var baselineByName = baseline
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var currentByName = current
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        var changes = new List<BreakingChange>();

        foreach (var b in baseline)
        {
            if (currentByName.TryGetValue(b.Name, out var c))
            {
                if (b.Kind != c.Kind)
                {
                    changes.Add(new BreakingChange(
                        Kind: BreakingChangeKind.KindChanged,
                        Severity: BreakingChangeSeverity.Breaking,
                        Name: b.Name,
                        EntityKind: c.Kind,
                        Project: c.Project,
                        FilePath: c.FilePath,
                        Line: c.Line,
                        Details: $"Kind changed: {b.Kind} → {c.Kind}"));
                }
                else if (b.Accessibility != c.Accessibility)
                {
                    if (b.Accessibility == PublicApiAccessibility.Public &&
                        c.Accessibility == PublicApiAccessibility.Protected)
                    {
                        changes.Add(new BreakingChange(
                            Kind: BreakingChangeKind.AccessibilityNarrowed,
                            Severity: BreakingChangeSeverity.Breaking,
                            Name: b.Name,
                            EntityKind: c.Kind,
                            Project: c.Project,
                            FilePath: c.FilePath,
                            Line: c.Line,
                            Details: "Accessibility narrowed: Public → Protected"));
                    }
                    else if (b.Accessibility == PublicApiAccessibility.Protected &&
                             c.Accessibility == PublicApiAccessibility.Public)
                    {
                        changes.Add(new BreakingChange(
                            Kind: BreakingChangeKind.AccessibilityWidened,
                            Severity: BreakingChangeSeverity.NonBreaking,
                            Name: b.Name,
                            EntityKind: c.Kind,
                            Project: c.Project,
                            FilePath: c.FilePath,
                            Line: c.Line,
                            Details: "Accessibility widened: Protected → Public"));
                    }
                }
            }
            else
            {
                changes.Add(new BreakingChange(
                    Kind: BreakingChangeKind.Removed,
                    Severity: BreakingChangeSeverity.Breaking,
                    Name: b.Name,
                    EntityKind: b.Kind,
                    Project: b.Project,
                    FilePath: b.FilePath,
                    Line: b.Line,
                    Details: $"{b.Kind} '{b.Name}' removed from {b.Project}"));
            }
        }

        foreach (var c in current)
        {
            if (!baselineByName.ContainsKey(c.Name))
            {
                changes.Add(new BreakingChange(
                    Kind: BreakingChangeKind.Added,
                    Severity: BreakingChangeSeverity.NonBreaking,
                    Name: c.Name,
                    EntityKind: c.Kind,
                    Project: c.Project,
                    FilePath: c.FilePath,
                    Line: c.Line,
                    Details: $"{c.Kind} '{c.Name}' added"));
            }
        }

        changes.Sort((a, b) =>
        {
            var bySeverity = ((int)a.Severity).CompareTo((int)b.Severity);
            if (bySeverity != 0) return bySeverity;
            return string.CompareOrdinal(a.Name, b.Name);
        });

        var byKind = changes
            .GroupBy(c => c.Kind.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var bySeverity = changes
            .GroupBy(c => c.Severity.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var summary = new BreakingChangesSummary(
            TotalChanges: changes.Count,
            ByKind: byKind,
            BySeverity: bySeverity);

        return new FindBreakingChangesResult(summary, changes);
    }

    private static IReadOnlyList<PublicApiEntry> LoadBaseline(string baselinePath)
    {
        if (!File.Exists(baselinePath))
            throw new FileNotFoundException($"Baseline not found: {baselinePath}", baselinePath);

        var ext = Path.GetExtension(baselinePath);
        if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
            return LoadBaselineFromJson(baselinePath);
        if (string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase))
            return LoadBaselineFromAssembly(baselinePath);

        throw new InvalidOperationException(
            $"Unsupported baseline file extension: '{ext}'. Expected .json or .dll.");
    }

    private static IReadOnlyList<PublicApiEntry> LoadBaselineFromJson(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var result = JsonSerializer.Deserialize<GetPublicApiSurfaceResult>(json, JsonOpts);
            return result?.Entries ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Baseline JSON is not a valid get_public_api_surface result: {ex.Message}", ex);
        }
    }

    private static IReadOnlyList<PublicApiEntry> LoadBaselineFromAssembly(string path)
    {
        var reference = MetadataReference.CreateFromFile(path);
        var compilation = CSharpCompilation.Create(
            "baseline-extraction",
            references: [reference]);

        if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
            throw new InvalidOperationException(
                $"Failed to load assembly: {path}");

        var projectName = Path.GetFileNameWithoutExtension(path);
        return PublicApiSurfaceExtractor.Extract(assembly, projectName, requireSourceLocation: false);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        opts.Converters.Add(new JsonStringEnumConverter());
        return opts;
    }
}
```

**Step 4: Run the tests**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindBreakingChangesToolTests" -v normal
```

Expected: all 14 tests pass.

**Common debugging:**
- If JSON deserialization fails: ensure `JsonNamingPolicy.CamelCase` is set; the `PublicApiEntry` properties are PascalCase in C# but serialized lowercase per the consumer's expectations.
- If JsonStringEnumConverter is missing: enums (`PublicApiKind`, `PublicApiAccessibility`) need it for round-tripping.
- If the assembly path test crashes with "Could not load file": make sure the synthetic baseline includes core .NET reference assemblies in `refs`.
- If the JSON identity roundtrip produces unexpected changes: check that `WriteBaselineJson` writes the same shape `LoadBaselineFromJson` reads (especially the `Entries` field, which deserializes via the same options).

**Step 5: Run full suite**

```bash
dotnet test
```

Expected: all green.

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Tools/FindBreakingChangesLogic.cs \
  tests/RoslynCodeLens.Tests/Tools/FindBreakingChangesToolTests.cs
git commit -m "feat: add FindBreakingChangesLogic with five change-kind diff"
```

---

## Task 4: `FindBreakingChangesTool` MCP wrapper

Thin wrapper, auto-registered.

**Files:**
- Create: `src/RoslynCodeLens/Tools/FindBreakingChangesTool.cs`

**Step 1: Create the wrapper**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindBreakingChangesTool
{
    [McpServerTool(Name = "find_breaking_changes")]
    [Description(
        "Diff the current solution's public API surface against a baseline (JSON snapshot " +
        "from a prior get_public_api_surface run, or a baseline .dll file). Reports five " +
        "change kinds: Removed/KindChanged/AccessibilityNarrowed (Breaking) plus " +
        "Added/AccessibilityWidened (NonBreaking). Returns a summary plus a per-change list " +
        "(kind, severity, fully-qualified name, entity kind, project, file, line, details). " +
        "Sort: Breaking before NonBreaking, then name ASC. " +
        "Limitations: return type changes, sealed-ness changes, and nullable annotation " +
        "changes are not detected (PublicApiEntry schema doesn't capture them).")]
    public static FindBreakingChangesResult Execute(
        MultiSolutionManager manager,
        [Description("Path to a baseline .json snapshot (from a prior get_public_api_surface call) or a baseline .dll file.")]
        string baselinePath)
    {
        manager.EnsureLoaded();
        return FindBreakingChangesLogic.Execute(
            manager.GetLoadedSolution(),
            manager.GetResolver(),
            baselinePath);
    }
}
```

**Step 2: Build the whole solution**

```bash
dotnet build
```

Expected: 0 errors. Auto-registration via `Program.cs:35`.

**Step 3: Run targeted tests**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindBreakingChangesToolTests" -v normal
```

Expected: 14/14 pass (still — the wrapper doesn't change Logic behavior).

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Tools/FindBreakingChangesTool.cs
git commit -m "feat: register find_breaking_changes MCP tool"
```

---

## Task 5: Add benchmark

**Files:**
- Modify: `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

**Step 1: Read the file** and find the existing `find_*` / `get_*` benchmarks. Find the `GlobalSetup` method that initializes `_loaded` and `_resolver`.

**Step 2: Add a setup field for the baseline JSON path** at the top of the class:

```csharp
private string _breakingChangesBaselinePath = null!;
```

In `GlobalSetup`, after `_resolver` is initialized, add:

```csharp
// Pre-generate a baseline JSON snapshot once for find_breaking_changes benchmarks.
var surface = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);
var opts = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
_breakingChangesBaselinePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
File.WriteAllText(_breakingChangesBaselinePath, System.Text.Json.JsonSerializer.Serialize(surface, opts));
```

Optionally add `[GlobalCleanup]` to delete the file. If `GlobalCleanup` doesn't exist yet on the class, add it; otherwise extend it:

```csharp
[GlobalCleanup]
public void Cleanup()
{
    if (File.Exists(_breakingChangesBaselinePath))
        File.Delete(_breakingChangesBaselinePath);
}
```

**Step 3: Add the benchmark method** alongside the others:

```csharp
[Benchmark(Description = "find_breaking_changes: JSON identity roundtrip")]
public object FindBreakingChanges()
{
    return FindBreakingChangesLogic.Execute(_loaded, _resolver, _breakingChangesBaselinePath);
}
```

**Step 4: Build the benchmarks project**

```bash
dotnet build benchmarks/RoslynCodeLens.Benchmarks -c Release
```

Expected: 0 errors.

**Step 5: Commit**

```bash
git add benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs
git commit -m "bench: add find_breaking_changes benchmark"
```

---

## Task 6: Update SKILL.md, README, CLAUDE.md

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
- Modify: `README.md`
- Modify: `CLAUDE.md`

**Step 1: Read SKILL.md** and add the tool. Mirror how `get_public_api_surface` is integrated.

For Red Flags routing table:
> | "Will this break consumers?" / "Show me breaking changes vs the previous release" / "Diff this build's API against baseline" | `find_breaking_changes` |

For Quick Reference table:
> | `find_breaking_changes` | "Will this break consumers?" |

For the relevant API/Inspection section, near `get_public_api_surface`:
> - `find_breaking_changes` — Diff the current public API surface against a baseline (JSON snapshot from `get_public_api_surface`, or a `.dll` file). Reports Removed/Added/KindChanged/AccessibilityNarrowed/Widened with Breaking/NonBreaking severity. Static analysis; doesn't currently detect return-type, sealed-ness, or nullable changes.

NOTE: do NOT add a metadata-support row.

**Step 2: Read README.md** and add to Features list near `get_public_api_surface`:

> - **find_breaking_changes** — Diff the current API against a baseline JSON or DLL; report removed members, kind changes, and accessibility changes with Breaking/NonBreaking severity.

**Step 3: Update `CLAUDE.md`** — change "27 code intelligence tools" to "28 code intelligence tools".

**Step 4: Sanity check**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindBreakingChangesToolTests" -v normal
```

Expected: 14/14 pass.

**Step 5: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md \
  README.md \
  CLAUDE.md
git commit -m "docs: announce find_breaking_changes in SKILL.md, README, CLAUDE.md"
```

---

## Done

After Task 6 the branch should have ~8 commits (design + plan + 6 implementation tasks), all tests green, the benchmark project compiling, and the tool auto-registered. From there: `/requesting-code-review` → PR.
