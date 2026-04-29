# `get_public_api_surface` Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a new MCP tool `get_public_api_surface` that enumerates every `public` and `protected` type and member declared in production projects, returning a deterministic flat list (sorted by name ASC, ordinal) plus per-kind/per-project/per-accessibility summary buckets.

**Architecture:** Recursive walk through every compilation's `Assembly.GlobalNamespace`. Per public type: emit a Type entry, then walk its `GetMembers()` and emit a Member entry for each accessible non-implicit member. Skip test projects (`TestProjectDetector`), generated source (`GeneratedCodeDetector`), compiler-generated members (`IsImplicitlyDeclared`), inherited members (only declared), and `protected` members on sealed types (unreachable). Sort the flat list by name ordinal. The same engine in a future PR will feed `find_breaking_changes`.

**Tech Stack:** Roslyn (`Microsoft.CodeAnalysis`), xUnit, BenchmarkDotNet, ModelContextProtocol.Server.

**Reference design:** `docs/plans/2026-04-29-get-public-api-surface-design.md`

**Patterns to mirror (read these before starting):**
- Tool wrapper: `src/RoslynCodeLens/Tools/FindDisposableMisuseTool.cs`
- Logic class: `src/RoslynCodeLens/Tools/FindUncoveredSymbolsLogic.cs` (especially the `EnumerateTypes` / `EnumerateNestedTypes` / `EnumerateCandidates` recursion — same shape needed here)
- Test pattern: `tests/RoslynCodeLens.Tests/Tools/FindDisposableMisuseToolTests.cs`
- MCP auto-registration: `src/RoslynCodeLens/Program.cs:35` uses `WithToolsFromAssembly()` — no `Program.cs` edit needed

---

## Task 1: Add fixture types for kind coverage

The existing fixture (TestLib + the various Fixture projects) covers Class, Interface, Method, Property, Field, Constructor. We need new types to cover Record, RecordStruct, Enum, Delegate, Indexer, Operator, sealed-protected (negative), abstract-protected (positive), and an internal type (negative).

**Files:**
- Create: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/PublicApiSamples.cs`

**Step 1: Create `PublicApiSamples.cs`**

```csharp
namespace TestLib;

// Public record class — kind: Record, plus its positional properties (Id, Name) as kind: Property
public record OrderRecord(int Id, string Name);

// Public record struct — kind: RecordStruct, plus positional properties (X, Y)
public record struct PointStruct(int X, int Y);

// Public enum — kind: Enum
public enum PriorityLevel
{
    Low,
    High
}

// Public delegate — kind: Delegate
public delegate void OrderProcessedHandler(int orderId);

// Public abstract class — protected members ARE part of API (subclassable from outside)
public abstract class AbstractProcessor
{
    public void Run() { }
    protected abstract void Process();
    protected int Counter;
}

// Public sealed class — protected members are NOT reachable, must be excluded
public sealed class SealedHolder
{
    public int PublicProp { get; set; }
    protected void HiddenProtected() { }   // EXCLUDED — sealed type's protected is unreachable
}

// Public class with indexer + user-defined operator
public class IndexerSample
{
    public string this[int index] => $"Item {index}";

    public static IndexerSample operator +(IndexerSample a, IndexerSample b)
        => new IndexerSample();
}

// Internal type — entire type and its members must be excluded (not public API)
internal class InternalHidden
{
    public void NotApi() { }
}
```

**Step 2: Build the fixture solution**

```bash
dotnet build tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestSolution.slnx -c Debug
```

Expected: 0 errors. The compiler may emit CA-style warnings on `Counter` (unused field) and `HiddenProtected` (unused method) — those are fine; they exist purely as fixture cases.

**Step 3: Run existing test suite to ensure no regressions**

```bash
dotnet test tests/RoslynCodeLens.Tests
```

Expected: same pass count as before this task. (Existing tools that walk public symbols may now see a few new types — `find_uncovered_symbols` for instance — but the assertions in those tests are about specific known symbols and shouldn't trip on additions.)

If any test newly fails, halt and report. Do NOT commit on a regression.

**Step 4: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/PublicApiSamples.cs
git commit -m "test: add PublicApiSamples fixture types (record, enum, delegate, indexer, operator, sealed/abstract protected)"
```

---

## Task 2: Models

Five files: two enums + three records.

**Files:**
- Create: `src/RoslynCodeLens/Models/PublicApiKind.cs`
- Create: `src/RoslynCodeLens/Models/PublicApiAccessibility.cs`
- Create: `src/RoslynCodeLens/Models/PublicApiEntry.cs`
- Create: `src/RoslynCodeLens/Models/PublicApiSummary.cs`
- Create: `src/RoslynCodeLens/Models/GetPublicApiSurfaceResult.cs`

**Step 1: `PublicApiKind.cs`**

```csharp
namespace RoslynCodeLens.Models;

public enum PublicApiKind
{
    // Types
    Class,
    Struct,
    Interface,
    Enum,
    Record,
    RecordStruct,
    Delegate,
    // Members
    Constructor,
    Method,
    Property,
    Indexer,
    Field,
    Event,
    Operator
}
```

**Step 2: `PublicApiAccessibility.cs`**

```csharp
namespace RoslynCodeLens.Models;

public enum PublicApiAccessibility
{
    Public,
    Protected
}
```

**Step 3: `PublicApiEntry.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record PublicApiEntry(
    PublicApiKind Kind,
    string Name,
    PublicApiAccessibility Accessibility,
    string Project,
    string FilePath,
    int Line);
```

**Step 4: `PublicApiSummary.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record PublicApiSummary(
    int TotalEntries,
    IReadOnlyDictionary<string, int> ByKind,
    IReadOnlyDictionary<string, int> ByProject,
    IReadOnlyDictionary<string, int> ByAccessibility);
```

**Step 5: `GetPublicApiSurfaceResult.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record GetPublicApiSurfaceResult(
    PublicApiSummary Summary,
    IReadOnlyList<PublicApiEntry> Entries);
```

**Step 6: Build to verify**

```bash
dotnet build src/RoslynCodeLens
```

Expected: 0 errors.

**Step 7: Commit**

```bash
git add src/RoslynCodeLens/Models/PublicApiKind.cs \
  src/RoslynCodeLens/Models/PublicApiAccessibility.cs \
  src/RoslynCodeLens/Models/PublicApiEntry.cs \
  src/RoslynCodeLens/Models/PublicApiSummary.cs \
  src/RoslynCodeLens/Models/GetPublicApiSurfaceResult.cs
git commit -m "feat: add models for get_public_api_surface output"
```

---

## Task 3: `GetPublicApiSurfaceLogic` + comprehensive tests

The detection engine. Recursive type walk + member walk + sort + summary. TDD with multiple test cases.

**Files:**
- Create: `src/RoslynCodeLens/Tools/GetPublicApiSurfaceLogic.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/GetPublicApiSurfaceToolTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/RoslynCodeLens.Tests/Tools/GetPublicApiSurfaceToolTests.cs
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GetPublicApiSurfaceToolTests : IAsyncLifetime
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
    [InlineData(PublicApiKind.Class)]
    [InlineData(PublicApiKind.Struct)]
    [InlineData(PublicApiKind.Interface)]
    [InlineData(PublicApiKind.Enum)]
    [InlineData(PublicApiKind.Record)]
    [InlineData(PublicApiKind.RecordStruct)]
    [InlineData(PublicApiKind.Delegate)]
    [InlineData(PublicApiKind.Constructor)]
    [InlineData(PublicApiKind.Method)]
    [InlineData(PublicApiKind.Property)]
    [InlineData(PublicApiKind.Indexer)]
    [InlineData(PublicApiKind.Field)]
    [InlineData(PublicApiKind.Event)]
    [InlineData(PublicApiKind.Operator)]
    public void EachKind_HasAtLeastOneEntry(PublicApiKind kind)
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        Assert.Contains(result.Entries, e => e.Kind == kind);
    }

    [Fact]
    public void Result_ContainsKnownPublicTypes()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        Assert.Contains(result.Entries, e => e.Name == "TestLib.Greeter" && e.Kind == PublicApiKind.Class);
        Assert.Contains(result.Entries, e => e.Name == "TestLib.IGreeter" && e.Kind == PublicApiKind.Interface);
        Assert.Contains(result.Entries, e => e.Name == "TestLib.FancyGreeter" && e.Kind == PublicApiKind.Class);
        Assert.Contains(result.Entries, e => e.Name == "TestLib.OrderRecord" && e.Kind == PublicApiKind.Record);
        Assert.Contains(result.Entries, e => e.Name == "TestLib.PointStruct" && e.Kind == PublicApiKind.RecordStruct);
        Assert.Contains(result.Entries, e => e.Name == "TestLib.PriorityLevel" && e.Kind == PublicApiKind.Enum);
        Assert.Contains(result.Entries, e => e.Name == "TestLib.OrderProcessedHandler" && e.Kind == PublicApiKind.Delegate);
    }

    [Fact]
    public void Result_RecordPositionalProperty_IsIncluded()
    {
        // OrderRecord(int Id, string Name) — Id and Name are public properties
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        Assert.Contains(result.Entries, e =>
            e.Kind == PublicApiKind.Property &&
            e.Name.EndsWith(".OrderRecord.Id", StringComparison.Ordinal));
        Assert.Contains(result.Entries, e =>
            e.Kind == PublicApiKind.Property &&
            e.Name.EndsWith(".OrderRecord.Name", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_DoesNotIncludeInternalTypes()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.Entries, e => e.Name.EndsWith(".InternalHidden", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Entries, e => e.Name.EndsWith(".NotApi", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_DoesNotIncludeProtectedOnSealed()
    {
        // SealedHolder.HiddenProtected — sealed type's protected is unreachable, excluded
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.Entries, e =>
            e.Name.EndsWith(".SealedHolder.HiddenProtected", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_IncludesProtectedOnNonSealed()
    {
        // AbstractProcessor.Process and AbstractProcessor.Counter — protected on abstract class, INCLUDED
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        Assert.Contains(result.Entries, e =>
            e.Name.EndsWith(".AbstractProcessor.Process", StringComparison.Ordinal) &&
            e.Accessibility == PublicApiAccessibility.Protected);
        Assert.Contains(result.Entries, e =>
            e.Name.EndsWith(".AbstractProcessor.Counter", StringComparison.Ordinal) &&
            e.Accessibility == PublicApiAccessibility.Protected);
    }

    [Fact]
    public void Result_DoesNotIncludeTestProjectMembers()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        var testProjectNames = TestDiscovery.TestProjectDetector
            .GetTestProjectIds(_loaded.Solution)
            .Select(id => _loaded.Solution.GetProject(id)!.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(result.Entries, e => testProjectNames.Contains(e.Project));
    }

    [Fact]
    public void Result_DoesNotIncludeImplicitMembers()
    {
        // Record auto-synthesizes Equals, GetHashCode, EqualityContract, ToString, Deconstruct, etc.
        // These are IsImplicitlyDeclared = true (the synthesized ones; positional properties are NOT implicit).
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        // EqualityContract is the canonical record-synthesized property — must NOT appear.
        Assert.DoesNotContain(result.Entries, e =>
            e.Name.EndsWith(".OrderRecord.EqualityContract", StringComparison.Ordinal));
    }

    [Fact]
    public void Entries_SortedByNameAscending()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        for (int i = 1; i < result.Entries.Count; i++)
        {
            Assert.True(
                string.CompareOrdinal(result.Entries[i - 1].Name, result.Entries[i].Name) <= 0,
                $"Sort violation at index {i}: '{result.Entries[i - 1].Name}' > '{result.Entries[i].Name}'");
        }
    }

    [Fact]
    public void Summary_TotalMatchesListLength()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        Assert.Equal(result.Entries.Count, result.Summary.TotalEntries);
    }

    [Fact]
    public void Summary_ByKindCountsAreCorrect()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        foreach (var (kindName, count) in result.Summary.ByKind)
        {
            var actual = result.Entries.Count(e => e.Kind.ToString() == kindName);
            Assert.Equal(actual, count);
        }
    }

    [Fact]
    public void Summary_ByProjectCountsAreCorrect()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        foreach (var (projectName, count) in result.Summary.ByProject)
        {
            var actual = result.Entries.Count(e => e.Project == projectName);
            Assert.Equal(actual, count);
        }
    }

    [Fact]
    public void Summary_ByAccessibilityCountsAreCorrect()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        foreach (var (accName, count) in result.Summary.ByAccessibility)
        {
            var actual = result.Entries.Count(e => e.Accessibility.ToString() == accName);
            Assert.Equal(actual, count);
        }
    }

    [Fact]
    public void Entries_HaveLocationInfo()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        foreach (var e in result.Entries)
        {
            Assert.NotEmpty(e.FilePath);
            Assert.True(e.Line > 0);
            Assert.NotEmpty(e.Project);
            Assert.NotEmpty(e.Name);
        }
    }
}
```

**Step 2: Run to verify they fail (compile errors)**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetPublicApiSurfaceToolTests" -v normal
```

Expected: compile errors — `GetPublicApiSurfaceLogic` doesn't exist.

**Step 3: Create `GetPublicApiSurfaceLogic.cs`**

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

            foreach (var type in EnumerateTypes(compilation.Assembly.GlobalNamespace))
            {
                if (!IsApiVisibleType(type)) continue;

                entries.Add(BuildTypeEntry(type, projectName));

                // Walk members. Skip nested types — they appear via the type-recursion.
                var protectedReachable = type.TypeKind == TypeKind.Class && !type.IsSealed;
                foreach (var member in type.GetMembers())
                {
                    if (member is INamedTypeSymbol) continue;
                    if (member.IsImplicitlyDeclared) continue;

                    var apiAcc = ClassifyMemberAccessibility(member, protectedReachable);
                    if (apiAcc is null) continue;

                    if (!HasInSourceLocation(member)) continue;
                    if (IsInGeneratedFile(member)) continue;

                    var entry = BuildMemberEntry(member, apiAcc.Value, projectName);
                    if (entry is not null)
                        entries.Add(entry);
                }
            }
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

    private static bool IsApiVisibleType(INamedTypeSymbol type)
    {
        if (type.IsImplicitlyDeclared) return false;
        if (type.DeclaredAccessibility != Accessibility.Public) return false;
        if (!HasInSourceLocation(type)) return false;
        if (IsInGeneratedFile(type)) return false;
        return true;
    }

    private static bool HasInSourceLocation(ISymbol symbol)
        => symbol.Locations.Any(l => l.IsInSource);

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

    private static PublicApiEntry BuildTypeEntry(INamedTypeSymbol type, string projectName)
    {
        var location = type.Locations.First(l => l.IsInSource);
        var lineSpan = location.GetLineSpan();

        return new PublicApiEntry(
            Kind: TypeKindToApiKind(type),
            Name: FullyQualified(type),
            Accessibility: PublicApiAccessibility.Public,  // type is public (non-public types are filtered out earlier)
            Project: projectName,
            FilePath: lineSpan.Path,
            Line: lineSpan.StartLinePosition.Line + 1);
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

    private static PublicApiEntry? BuildMemberEntry(ISymbol member, PublicApiAccessibility apiAcc, string projectName)
    {
        var location = member.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null) return null;

        var (kind, name) = MemberKindAndName(member);
        if (kind is null) return null;

        var lineSpan = location.GetLineSpan();
        return new PublicApiEntry(
            Kind: kind.Value,
            Name: name,
            Accessibility: apiAcc,
            Project: projectName,
            FilePath: lineSpan.Path,
            Line: lineSpan.StartLinePosition.Line + 1);
    }

    private static (PublicApiKind? Kind, string Name) MemberKindAndName(ISymbol member)
    {
        var fqn = FullyQualified(member);

        switch (member)
        {
            case IMethodSymbol method:
                return method.MethodKind switch
                {
                    MethodKind.Constructor => (PublicApiKind.Constructor, fqn),
                    MethodKind.UserDefinedOperator => (PublicApiKind.Operator, fqn),
                    MethodKind.Conversion => (PublicApiKind.Operator, fqn),
                    MethodKind.Ordinary => (PublicApiKind.Method, fqn),
                    // Property/event accessors are reported via the property/event entry; skip.
                    MethodKind.PropertyGet or MethodKind.PropertySet
                        or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise
                        or MethodKind.Destructor
                        or MethodKind.StaticConstructor => (null, fqn),
                    _ => (null, fqn)
                };

            case IPropertySymbol property:
                return property.IsIndexer
                    ? (PublicApiKind.Indexer, fqn)
                    : (PublicApiKind.Property, fqn);

            case IFieldSymbol:
                return (PublicApiKind.Field, fqn);

            case IEventSymbol:
                return (PublicApiKind.Event, fqn);

            default:
                return (null, fqn);
        }
    }

    private static string FullyQualified(ISymbol symbol)
        => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal);
}
```

**Step 4: Run targeted tests**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetPublicApiSurfaceToolTests" -v normal
```

Expected: all pass (14 [Theory] cases + 12 [Fact]s = 26 total).

**Common debugging:**
- If a kind isn't found: `EachKind_HasAtLeastOneEntry` will fail with the specific kind. Check the fixture provides it AND the kind classification handles it.
- If `EqualityContract` appears: `IsImplicitlyDeclared` should be true for it; verify the filter is in the right place.
- If `Counter` field isn't recognized as protected: confirm `ClassifyMemberAccessibility` handles `Accessibility.Protected` (not just `ProtectedOrInternal`).
- If sort fails: ensure `string.CompareOrdinal` is used, not culture-aware comparison.

**Step 5: Run full suite**

```bash
dotnet test
```

Expected: all green (modulo occasional pre-existing flakies).

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Tools/GetPublicApiSurfaceLogic.cs \
  tests/RoslynCodeLens.Tests/Tools/GetPublicApiSurfaceToolTests.cs
git commit -m "feat: add GetPublicApiSurfaceLogic with public/protected enumeration"
```

---

## Task 4: `GetPublicApiSurfaceTool` MCP wrapper

Thin wrapper, auto-registered.

**Files:**
- Create: `src/RoslynCodeLens/Tools/GetPublicApiSurfaceTool.cs`

**Step 1: Create the wrapper**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetPublicApiSurfaceTool
{
    [McpServerTool(Name = "get_public_api_surface")]
    [Description(
        "Enumerate every public and protected type and member declared in production " +
        "projects of the active solution. Returns a deterministically-sorted (name ASC) " +
        "flat list of API entries (kind, fully-qualified name, accessibility, project, " +
        "file, line) plus per-kind/per-project/per-accessibility summary buckets. " +
        "Skips test projects, generated code, compiler-generated members, internal " +
        "symbols, and protected members on sealed types (unreachable). Inherited " +
        "members are not repeated under derived types — only declared members appear.")]
    public static GetPublicApiSurfaceResult Execute(MultiSolutionManager manager)
    {
        manager.EnsureLoaded();
        return GetPublicApiSurfaceLogic.Execute(
            manager.GetLoadedSolution(),
            manager.GetResolver());
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
dotnet test tests/RoslynCodeLens.Tests --filter "GetPublicApiSurfaceToolTests" -v normal
```

Expected: 26/26 pass (still — the wrapper doesn't change Logic behavior).

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Tools/GetPublicApiSurfaceTool.cs
git commit -m "feat: register get_public_api_surface MCP tool"
```

---

## Task 5: Add benchmark

**Files:**
- Modify: `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

**Step 1: Read the file** and find the existing `find_*` / `get_*` benchmarks.

**Step 2: Add the benchmark method** alongside the others:

```csharp
[Benchmark(Description = "get_public_api_surface: whole solution")]
public object GetPublicApiSurface()
{
    return GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);
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
git commit -m "bench: add get_public_api_surface benchmark"
```

---

## Task 6: Update SKILL.md, README, CLAUDE.md

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
- Modify: `README.md`
- Modify: `CLAUDE.md`

**Step 1: Read SKILL.md** and add the tool.

For Red Flags routing table:
> | "What's the public API of this library?" / "Show me the API surface" / "I need a PublicAPI.txt-style snapshot" | `get_public_api_surface` |

For Quick Reference table:
> | `get_public_api_surface` | "What's the public API of this library?" |

For the relevant Code Quality / Inspection section, near `get_symbol_context`:
> - `get_public_api_surface` — Enumerate every public/protected type and member declared in production projects; deterministically sorted; suitable for API review or breaking-change baselines. Static analysis; only declared (not inherited) members appear.

NOTE: do NOT add a metadata-support row.

**Step 2: Read README.md** and add to Features list near `get_symbol_context`:

> - **get_public_api_surface** — Enumerate every public/protected type and member in production projects; flat, deterministically-sorted list suitable for API review or breaking-change baselines.

**Step 3: Update `CLAUDE.md`** — change "26 code intelligence tools" to "27 code intelligence tools".

**Step 4: Sanity check**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetPublicApiSurfaceToolTests" -v normal
```

Expected: 26/26 pass.

**Step 5: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md \
  README.md \
  CLAUDE.md
git commit -m "docs: announce get_public_api_surface in SKILL.md, README, CLAUDE.md"
```

---

## Done

After Task 6 the branch should have ~7 commits (design + plan from before + 6 implementation tasks), all tests green, the benchmark project compiling, and the tool auto-registered. From there: `/requesting-code-review` → PR.
