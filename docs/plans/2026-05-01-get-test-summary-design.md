# `get_test_summary` Design

**Status:** Approved 2026-05-01

## Goal

Per-project inventory of test methods. Each test reports framework, attribute kind, data-driven row count, location, and the production symbols it references. Complements `find_tests_for_symbol` (test → production); this goes project → tests.

## Use cases

- "What does this test suite cover?" — see referenced production symbols across all tests in a project.
- "How is the test framework distribution split?" — `ByFramework` counts tell you "45 xUnit, 12 NUnit, 8 MSTest."
- "Show me all `[Theory]` tests with row counts" — agent filters by attribute + sorts by `InlineDataRowCount`.

## API

```csharp
GetTestSummaryResult Execute(string? project = null)
```

`project` filters to a single test project (case-insensitive). Default = all test projects in the solution.

## Output

```csharp
public record GetTestSummaryResult(
    IReadOnlyList<ProjectTestSummary> Projects);

public record ProjectTestSummary(
    string Project,
    int TotalTests,
    IReadOnlyDictionary<string, int> ByFramework,   // "XUnit" → 45, ...
    IReadOnlyDictionary<string, int> ByAttribute,   // "Fact" → 30, "Theory" → 15, ...
    IReadOnlyList<TestMethodSummary> Tests);

public record TestMethodSummary(
    string MethodName,                // fully-qualified
    string Framework,                 // "XUnit" / "NUnit" / "MSTest"
    string AttributeShortName,        // "Fact" / "Theory" / "Test" / "TestCase" / "TestMethod" / "DataTestMethod"
    int InlineDataRowCount,           // 0 for non-data-driven; N for [InlineData]/[TestCase]/[DataRow]
    IReadOnlyList<string> ReferencedSymbols,
    string FilePath,
    int Line);
```

## What counts as a "referenced symbol"

Walk the test method body. For each `InvocationExpressionSyntax` / `ObjectCreationExpressionSyntax` / `MemberAccessExpressionSyntax`:
- Resolve via `SemanticModel.GetSymbolInfo` to `IMethodSymbol` / `IPropertySymbol` / `IFieldSymbol`.
- **Exclude** anything in framework or BCL namespaces:
  - `Xunit.*`
  - `NUnit.Framework.*`
  - `Microsoft.VisualStudio.TestTools.UnitTesting.*`
  - `System.*` and `Microsoft.*` (BCL)
- Distinct, sorted ordinal — keeps output stable.

Surface form: `IMethodSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)` minus `global::`.

## InlineData / TestCase / DataRow row count

Count attributes on the test method whose name matches the framework's data-row attribute:
- xUnit: `[InlineData]`
- NUnit: `[TestCase]`
- MSTest: `[DataRow]`

For non-data-driven tests, `InlineDataRowCount: 0`.

## Architecture

1. **Detect test projects** — `TestProjectDetector.GetTestProjectIds(loaded.Solution)`. If `project` argument is non-null, narrow to that one (case-insensitive `Project.Name` match).
2. **Walk methods** — for each test project's compilation, recursively enumerate types (`compilation.GlobalNamespace`), find methods, filter by `TestMethodClassifier.IsTestMethod`.
3. **Classify** — `TestMethodClassifier.Classify(method)` returns the framework + attribute short name.
4. **Count data rows** — walk attributes again, count those whose attribute-class short name matches the framework's data-row attribute.
5. **Resolve referenced symbols** — get the method's body syntax, walk descendants, resolve symbols via `SemanticModel`, filter framework/BCL.
6. **Group + sort + build counts** — sort tests within project by `(FilePath, Line)` ordinal; sort projects by name ASC; build `ByFramework`/`ByAttribute` dictionaries.

## Scope decisions

| Concern | Decision |
|---|---|
| Production projects | Excluded — only test projects scanned |
| Test fixtures (`MSTestFixture`, etc.) | Included — they're test-shaped projects with declared test methods |
| Generated code | Skipped (consistent with other audit tools) |
| Inherited tests | Counted at the inheriting type only if it declares the attribute locally |
| Async tests | Same handling as sync — async-ness not surfaced |
| Disabled tests (`[Fact(Skip = "…")]`) | Counted normally — agent can filter client-side |

## Edge cases

| Case | Handling |
|---|---|
| Test method calling another test method | Production-symbol filter excludes it (other test methods are in same test project, so `IsTestMethod` true → not a production reference). Or: include if not classified as a test method itself. Choose: exclude — they're test scaffolding. |
| Method with no body (interface declaration / abstract) | Skipped (not a runnable test) |
| Test with `[InlineData]` count = 0 (parameterless `[Theory]`) | `InlineDataRowCount: 0` (degenerate) |
| Symbol resolves to a delegate-typed field/property | Counted; the field's FQN is recorded |

## Performance

`TestProjectDetector.GetTestProjectIds` is cached and O(P). Per-test-method body walks are bounded by method size; symbol resolution is the dominant cost. Comparable to `find_tests_for_symbol` (transitive mode) per project.

Benchmark: `get_test_summary: whole solution`.

## MCP wrapper

Standard pattern matching `find_uncovered_symbols` (whole-solution audit composite):

```csharp
[McpServerToolType]
public static class GetTestSummaryTool
{
    [McpServerTool(Name = "get_test_summary")]
    [Description(...)]
    public static GetTestSummaryResult Execute(MultiSolutionManager manager, string? project = null)
    { ... }
}
```

Auto-registered via `WithToolsFromAssembly()`.

## Testing

Use existing `XUnitFixture` / `NUnitFixture` / `MSTestFixture` projects. They already have `SampleTests.cs` with `[Fact]`/`[Theory]`/`[Test]`/`[TestCase]`/`[TestMethod]`/`[DataRow]` patterns calling `Greeter.Greet`.

Tests:
- `Result_FindsXUnitTests`
- `Result_FindsNUnitTests`
- `Result_FindsMSTestTests`
- `InlineDataRowCount_PopulatedForXUnitTheory`
- `InlineDataRowCount_ZeroForFact`
- `ReferencedSymbols_IncludeProductionCalls`
- `ReferencedSymbols_ExcludesFrameworkAndBcl`
- `ProjectFilter_OnlyReturnsRequestedProject`
- `Result_OmitsProductionProjects` (TestLib / TestLib2 absent)
- `ByFramework_CountsAreCorrect`
- `ByAttribute_CountsAreCorrect`
- `Tests_SortedByFileLine`
- `UnknownProject_ReturnsEmptyList`

## Out of scope (deferred — append to BACKLOG.md)

- **Async-test flagging** — `IsAsync` could surface but isn't included now.
- **Skip-reason surface** for `[Fact(Skip = "…")]` / `[Ignore]` — agent can compute via `find_attribute_usages` if needed.
- **`[MemberData]` / `[ClassData]` row tracking** — only inline rows are counted; theory data-source attributes don't expose row count without runtime evaluation.
- **Cross-project test→production coverage map** — that's `find_tests_for_symbol` territory, used in reverse.

## File checklist

- `src/RoslynCodeLens/Tools/GetTestSummaryLogic.cs`
- `src/RoslynCodeLens/Tools/GetTestSummaryTool.cs`
- `src/RoslynCodeLens/Models/GetTestSummaryResult.cs`
- `src/RoslynCodeLens/Models/ProjectTestSummary.cs`
- `src/RoslynCodeLens/Models/TestMethodSummary.cs`
- `tests/RoslynCodeLens.Tests/Tools/GetTestSummaryToolTests.cs`
- `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs` — add benchmark
- `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` — Red Flags, Quick Reference, Test-aware
- `README.md` — Features list
- `CLAUDE.md` — bump tool count
- `docs/BACKLOG.md` — append deferred items
