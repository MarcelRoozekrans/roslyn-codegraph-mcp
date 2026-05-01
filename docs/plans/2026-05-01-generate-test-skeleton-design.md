# `generate_test_skeleton` Design

**Status:** Approved 2026-05-01

## Goal

Emit a compilable test-class skeleton for a method or type. The tool returns the skeleton as text plus a suggested file path; the agent decides whether and where to write it. Closes the loop between `find_uncovered_symbols` / `get_test_summary` (identify gaps) and writing the actual stub.

## Use cases

- "I just learned method X is uncovered (`find_uncovered_symbols`) — give me a stub I can fill in."
- "Generate a test class for type Y so I can drop it into the test project."
- "Bootstrap tests for a new service before the production code is finalized."

## API

```csharp
GenerateTestSkeletonResult Execute(
    string symbol,           // method or type FQN
    string? framework = null // null → auto-detect; "xunit" / "nunit" / "mstest" override
);
```

`symbol` resolution mirrors `find_references`: type FQN like `MyApp.Services.OrderService` → full test class; method FQN like `MyApp.Services.OrderService.PlaceOrder` → single test stub wrapped in a class.

## Output

```csharp
public record GenerateTestSkeletonResult(
    string Framework,                   // "XUnit" / "NUnit" / "MSTest"
    string SuggestedFilePath,           // tests/{TestProject}/{Namespace}/{TypeName}Tests.cs
    string ClassName,                   // "{TypeName}Tests"
    string Code,                        // full .cs file contents
    IReadOnlyList<string> TodoNotes);   // human-readable hints, e.g. "Constructor needs IFoo dependency — wire mock"
```

Single output object — no preview/apply split. The text *is* the preview. The agent uses the `Write` tool if it wants to commit it to disk.

## Framework auto-detect

1. Scan loaded solution for test projects via `TestProjectDetector.GetTestProjectIds`.
2. Pick the framework most-commonly declared across them (count test projects per framework — not test methods).
3. Tie → xUnit.
4. No test projects found → xUnit.
5. Explicit `framework` parameter overrides everything.

Framework-detection per project: reuse the namespace/attribute heuristics from `TestMethodClassifier`. A project is "xUnit" if it references `Xunit.*`, "NUnit" if it references `NUnit.Framework.*`, "MSTest" if it references `Microsoft.VisualStudio.TestTools.UnitTesting.*`.

## What stubs are emitted

For each method targeted (one for single-method input; every public method for type input):

- **Happy-path `[Fact]`** — instantiate SUT (`var sut = new TypeName(...)`), invoke target, `Assert.Equal(/* expected */, /* actual */)`. Body is TODO comments.
- **`[Theory]` with `[InlineData]`** — only when method has primitive parameters (string / numeric / bool / enum). One `[InlineData]` row of TODO values.
- **`[Fact]` per distinct exception type thrown directly in body** (`throw new {ExceptionType}(...)`) → `Assert.Throws<T>(() => sut.Method(...))`. Throws via helper methods are not detected.
- **Async detection** — methods returning `Task` / `Task<T>` → `public async Task` test name + `await`.
- **Static methods** — no SUT instantiation; call type directly: `TypeName.Method(...)`.

Naming convention: `{MethodName}_{Scenario}` for tests. Scenarios: `HappyPath`, `Throws{ExceptionTypeShortName}`, `Theory` (for parameterized).

## Class skeleton

- Class name: `{TypeName}Tests`.
- Namespace: `{ProductionNamespace}.Tests` if the matched test project's root namespace already follows that pattern; otherwise mirror the test project's namespace + the type's relative folder.
- Constructor with dependencies → no auto-wiring; emit `var sut = new TypeName(/* TODO: dependencies */);` and add a TodoNote per parameter listing its type.
- Generic types/methods → emit `TODO: pick type args` comment, instantiate with placeholder type args (`<object>`).

## Suggested file path

1. Find test projects that reference the production project containing the symbol. Single match → use it. Multiple → first by name (alphabetical).
2. Mirror source folder structure: `src/{Proj}/Services/OrderService.cs` → `tests/{Proj}.Tests/Services/OrderServiceTests.cs`.
3. No matched test project in solution → fallback to `tests/{Proj}.Tests/{TypeName}Tests.cs` (placeholder root); flag via TodoNote.

## Architecture

1. Resolve `symbol` via `SymbolResolver` to `INamedTypeSymbol` or `IMethodSymbol`. Bail with `SymbolNotFoundError` if neither.
2. Determine framework: explicit override → auto-detect → xUnit fallback.
3. Determine target methods: for type input → enumerate public instance + static methods (excluding properties, indexers, operators, ctors, inherited-from-object); for method input → just that method.
4. For each method, walk body via `SemanticModel`:
   - Collect distinct exception types from `ThrowStatementSyntax` / `ThrowExpressionSyntax`.
   - Inspect parameter list for "primitive" eligibility → `[Theory]` if any.
   - Inspect return type → async or sync.
5. Determine suggested file path (test project lookup + namespace mirroring).
6. Build C# source via plain string composition (no `SyntaxFactory`). Lines:
   - `using` block (production namespace + framework namespace).
   - `namespace` declaration.
   - `public class {Type}Tests { ... }` containing all stubs.
7. Return `GenerateTestSkeletonResult`.

String composition over `SyntaxFactory`: stubs are formulaic and fixed-shape, so the composed output is more readable than syntax-tree construction. Tests assert on substrings rather than parsed AST.

## Scope decisions

| Concern | Decision |
|---|---|
| Private/internal methods | Excluded — only public + protected |
| Inherited methods | Excluded from full-class generation; agent calls per-method explicitly if needed |
| Properties | Excluded — testing simple getters/setters is low-value |
| Constructors | Excluded from per-method enumeration; covered indirectly via SUT instantiation in other tests |
| Indexers / operators | Excluded |
| Generated source files | Excluded as inputs (`GeneratedCodeDetector.IsGenerated` skip) |
| Existing test methods | NOT checked — we generate the skeleton; agent merges/dedupes |
| File-write | NOT done by tool — text returned only |

## Edge cases

| Case | Handling |
|---|---|
| Type has no eligible public methods | Empty class skeleton with `// TODO: no public methods detected` comment + matching TodoNote |
| Method body throws same exception type 3× | Counted once — distinct exception types only |
| Method throws via helper (not direct `throw`) | Not detected — only direct `throw new {Type}(...)` |
| Async-void method | Treat as void; surface a TodoNote (`async void` is uncommon — verify return shape) |
| Symbol resolves but is in metadata (closed-source) | Bail — return error; closed-source types can't be tested directly |
| No production project for the symbol | Bail — `SymbolNotFoundError` |
| No test project in solution | Suggested path falls back to placeholder; TodoNote flags it |
| Generic type / generic method | Use `<object>` placeholder + TodoNote |
| Method is overloaded | Each overload gets its own stub: `{MethodName}_{Arity}args_HappyPath` |
| Type is `static class` | All methods are static; no SUT instantiation; no constructor TodoNotes |
| Type is `abstract` | Bail — can't `new` an abstract; TodoNote suggests using a derived type |
| Type is `sealed`, no parameterless ctor, all-private ctors | TodoNote about needing factory or `internal` access |

## Performance

- Symbol resolution: O(1) via existing index.
- Body walk per method: O(syntax-nodes-in-body), bounded by method size.
- Test-project scan: O(P), already cached by `TestProjectDetector`.
- Comparable to `analyze_method` in cost.

Benchmark: `generate_test_skeleton: type with N methods`.

## MCP wrapper

Standard pattern (auto-registered via `WithToolsFromAssembly`):

```csharp
[McpServerToolType]
public static class GenerateTestSkeletonTool
{
    [McpServerTool(Name = "generate_test_skeleton")]
    [Description(...)]
    public static GenerateTestSkeletonResult Execute(
        MultiSolutionManager manager, string symbol, string? framework = null)
    { ... }
}
```

## Testing

Use existing `XUnitFixture` / `NUnitFixture` / `MSTestFixture` projects + `TestLib` / `TestLib2` for production-side targets. Add small fixture types in `TestLib` if needed for edge cases (static class, abstract class, generic type).

Tests:
- `Method_GeneratesFactSkeleton`
- `Type_GeneratesClassWithFactPerPublicMethod`
- `MethodWithPrimitiveParams_GeneratesTheoryWithInlineData`
- `MethodReturningTask_GeneratesAsyncTest`
- `MethodThrowingException_GeneratesAssertThrowsStub`
- `StaticMethod_DoesNotInstantiateSut`
- `Framework_AutoDetectsXUnitFromTestProject`
- `Framework_OverrideHonored`
- `Type_ExcludesPrivateAndProperties`
- `Type_ExcludesConstructorsAndIndexers`
- `SuggestedPath_MirrorsSourceFolder`
- `TodoNotes_IncludeConstructorDependencies`
- `OverloadedMethod_GeneratesDistinctStubsPerArity`
- `AbstractType_ReturnsErrorOrTodoNote`
- `UnknownSymbol_ReturnsNotFoundError`
- `Code_IsSyntacticallyValidCSharp` (parse the result with Roslyn — must produce zero diagnostics)

## Out of scope (deferred → BACKLOG.md)

- **Property / indexer / operator stubs** — low value; agent can request manually.
- **Mock framework integration** (Moq, NSubstitute, FakeItEasy) — opinionated; agent picks.
- **Test data builders** (AutoFixture, Bogus) — same.
- **Cross-method dependency analysis** (helper methods called by SUT) — keep skeleton focused.
- **Inherited-member skeletons** — agent composes via `get_overloads` / hierarchy tools.
- **`SyntaxFactory`-based output** — string composition is cleaner for stub-shaped output.
- **Auto-detection of throw-helpers** — only direct `throw new T(...)` for now.
- **Detect existing tests and merge** — agent handles dedupe.

## File checklist

- `src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs`
- `src/RoslynCodeLens/Tools/GenerateTestSkeletonTool.cs`
- `src/RoslynCodeLens/Models/GenerateTestSkeletonResult.cs`
- `tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs`
- `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs` — add benchmark
- `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` — Quick Reference + Test-aware section
- `README.md` — Features list
- `CLAUDE.md` — bump tool count
- `docs/BACKLOG.md` — append deferred items, remove `generate_test_skeleton` from main backlog
