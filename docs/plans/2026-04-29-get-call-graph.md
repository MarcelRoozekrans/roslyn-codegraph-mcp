# `get_call_graph` Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a new MCP tool `get_call_graph` that returns the transitive caller and/or callee graph of a method symbol, depth-bounded with cycle detection. Output is an adjacency-list dict per direction.

**Architecture:** BFS traversal keyed by fully-qualified symbol name. Callees direction walks method bodies via `SemanticModel.GetSymbolInfo` on invocation/object-creation/property-access syntax. Callers direction uses `SymbolFinder.FindCallersAsync` per visited node. External (metadata) callees included as terminal leaves; declared signature only on callee side (no virtual dispatch resolution). Hard cap on total node count with `truncated` flag.

**Tech Stack:** Roslyn (`Microsoft.CodeAnalysis`, `Microsoft.CodeAnalysis.CSharp.Syntax`, `Microsoft.CodeAnalysis.FindSymbols`), xUnit, BenchmarkDotNet, ModelContextProtocol.Server.

**Reference design:** `docs/plans/2026-04-29-get-call-graph-design.md`

**Patterns to mirror (read these before starting):**
- Tool wrapper: `src/RoslynCodeLens/Tools/AnalyzeMethodTool.cs`
- Existing depth-1 callee walker: `src/RoslynCodeLens/Tools/AnalyzeMethodLogic.cs:27-72` (`FindOutgoingCalls`)
- Existing callers walker: `src/RoslynCodeLens/Tools/FindCallersLogic.cs`
- Test pattern: `tests/RoslynCodeLens.Tests/Tools/AnalyzeMethodToolTests.cs`
- MCP auto-registration: `src/RoslynCodeLens/Program.cs:35` uses `WithToolsFromAssembly()` — no `Program.cs` edit needed

---

## Task 1: Models

5 small files: 2 enums + 3 records.

**Files:**
- Create: `src/RoslynCodeLens/Models/CallGraphNodeKind.cs`
- Create: `src/RoslynCodeLens/Models/CallGraphEdgeKind.cs`
- Create: `src/RoslynCodeLens/Models/CallGraphEdge.cs`
- Create: `src/RoslynCodeLens/Models/CallGraphNode.cs`
- Create: `src/RoslynCodeLens/Models/GetCallGraphResult.cs`

**Step 1: `CallGraphNodeKind.cs`**

```csharp
namespace RoslynCodeLens.Models;

public enum CallGraphNodeKind
{
    Method,
    Property,
    Constructor,
    Operator
}
```

**Step 2: `CallGraphEdgeKind.cs`**

```csharp
namespace RoslynCodeLens.Models;

public enum CallGraphEdgeKind
{
    Method,
    PropertyGet,
    PropertySet,
    Constructor,
    Operator
}
```

**Step 3: `CallGraphEdge.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record CallGraphEdge(
    string Target,
    CallGraphEdgeKind EdgeKind);
```

**Step 4: `CallGraphNode.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record CallGraphNode(
    CallGraphNodeKind Kind,
    string Project,
    string FilePath,
    int Line,
    bool IsExternal,
    IReadOnlyList<CallGraphEdge> Edges);
```

**Step 5: `GetCallGraphResult.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record GetCallGraphResult(
    string Root,
    string Direction,
    int MaxDepthRequested,
    bool Truncated,
    IReadOnlyDictionary<string, CallGraphNode> Callees,
    IReadOnlyDictionary<string, CallGraphNode> Callers);
```

**Step 6: Build**

```bash
dotnet build src/RoslynCodeLens
```

Expected: 0 errors.

**Step 7: Commit**

```bash
git add src/RoslynCodeLens/Models/CallGraphNodeKind.cs \
  src/RoslynCodeLens/Models/CallGraphEdgeKind.cs \
  src/RoslynCodeLens/Models/CallGraphEdge.cs \
  src/RoslynCodeLens/Models/CallGraphNode.cs \
  src/RoslynCodeLens/Models/GetCallGraphResult.cs
git commit -m "feat: add models for get_call_graph"
```

---

## Task 2: Test fixture for transitive chains, cycles, and edge kinds

The existing `Greeter.cs` has a single chain (`OldGreet → Greet`). For `get_call_graph` we need fixtures that cover transitive chains (depth ≥ 3), a cycle, property accessors, constructors, and operator overloads — without polluting other tests.

**Files:**
- Create: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/CallGraphSamples.cs`

**Step 1: Create the fixture**

```csharp
using System;

namespace TestLib;

// Synthetic chains and shapes for get_call_graph tests. Keep this self-contained
// so adding/removing methods here doesn't ripple into other tools' tests.
public class CallGraphSamples
{
    public string Root() => Level1A() + Level1B();

    public string Level1A() => Level2();
    public string Level1B() => Level2();
    public string Level2() => Level3();
    public string Level3() => "leaf";

    // External call — should appear as a terminal leaf in the graph.
    public string CallsExternal() => string.Format("{0}", "x");

    // Cycle: A calls B, B calls A.
    public void CycleA() => CycleB();
    public void CycleB() => CycleA();

    // Property accessor and constructor edges.
    public string PropertyAndCtor()
    {
        var holder = new SampleHolder();
        var read = holder.Value;
        holder.Value = read + "x";
        return read;
    }

    // Operator overload edge.
    public Money UseOperator(Money a, Money b) => a + b;
}

public class SampleHolder
{
    public string Value { get; set; } = "";
}

public readonly record struct Money(int Cents)
{
    public static Money operator +(Money a, Money b) => new(a.Cents + b.Cents);
}
```

**Step 2: Build to verify the fixture compiles**

```bash
dotnet build tests/RoslynCodeLens.Tests
```

Expected: 0 errors.

**Step 3: Run existing tests to confirm nothing broke**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "AnalyzeMethodToolTests"
```

Expected: all green.

**Step 4: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/CallGraphSamples.cs
git commit -m "test: add CallGraphSamples fixture for transitive/cycle/edge-kind cases"
```

---

## Task 3: `GetCallGraphLogic` with comprehensive tests (TDD)

Core BFS engine. Both directions, cycle detection, truncation, edge classification.

**Files:**
- Create: `src/RoslynCodeLens/Tools/GetCallGraphLogic.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/GetCallGraphToolTests.cs`

**Step 1: Write the failing tests**

`tests/RoslynCodeLens.Tests/Tools/GetCallGraphToolTests.cs`:

```csharp
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GetCallGraphToolTests : IAsyncLifetime
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
    public void UnknownSymbol_ReturnsNull()
    {
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "Does.Not.Exist", "callees", 3, 500);

        Assert.Null(result);
    }

    [Fact]
    public void Callees_DepthOne_DirectCalleesAppear()
    {
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "CallGraphSamples.Root", "callees", 1, 500)!;

        Assert.NotNull(result);
        Assert.Equal("callees", result.Direction);
        Assert.Empty(result.Callers);

        // Root must be in the dict and have edges to Level1A and Level1B.
        var rootKey = Assert.Single(result.Callees, kv =>
            kv.Key.Contains("CallGraphSamples.Root", StringComparison.Ordinal)).Key;
        var rootNode = result.Callees[rootKey];
        Assert.Contains(rootNode.Edges, e => e.Target.Contains("Level1A", StringComparison.Ordinal));
        Assert.Contains(rootNode.Edges, e => e.Target.Contains("Level1B", StringComparison.Ordinal));
    }

    [Fact]
    public void Callees_DepthThree_FollowsTransitiveChain()
    {
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "CallGraphSamples.Root", "callees", 3, 500)!;

        // Root → Level1A → Level2 → Level3 (depth 3 reaches Level3)
        Assert.Contains(result.Callees, kv => kv.Key.Contains("Level1A", StringComparison.Ordinal));
        Assert.Contains(result.Callees, kv => kv.Key.Contains("Level2", StringComparison.Ordinal));
        Assert.Contains(result.Callees, kv => kv.Key.Contains("Level3", StringComparison.Ordinal));
    }

    [Fact]
    public void Callees_MaxDepthOne_StopsAtFirstLevel()
    {
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "CallGraphSamples.Root", "callees", 1, 500)!;

        // Level1A is at depth 1 (directly called from Root) → present.
        // Level2 is at depth 2 → must NOT be present (we don't expand Level1A).
        Assert.Contains(result.Callees, kv => kv.Key.Contains("Level1A", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Callees, kv => kv.Key.Contains("Level2", StringComparison.Ordinal));
    }

    [Fact]
    public void Callees_External_AppearsAsTerminalLeaf()
    {
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "CallGraphSamples.CallsExternal", "callees", 3, 500)!;

        // string.Format must appear and have IsExternal=true and no edges.
        var external = Assert.Single(result.Callees,
            kv => kv.Value.IsExternal && kv.Key.Contains("Format", StringComparison.Ordinal));
        Assert.Empty(external.Value.Edges);
        Assert.Equal("", external.Value.Project);
        Assert.Equal("", external.Value.FilePath);
    }

    [Fact]
    public void Callees_Cycle_BothNodesPresentAndEdgesClose()
    {
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "CallGraphSamples.CycleA", "callees", 5, 500)!;

        var aKey = Assert.Single(result.Callees, kv => kv.Key.Contains("CycleA", StringComparison.Ordinal)).Key;
        var bKey = Assert.Single(result.Callees, kv => kv.Key.Contains("CycleB", StringComparison.Ordinal)).Key;

        // A's edges include B; B's edges include A.
        Assert.Contains(result.Callees[aKey].Edges, e => e.Target == bKey);
        Assert.Contains(result.Callees[bKey].Edges, e => e.Target == aKey);
    }

    [Fact]
    public void Callees_Constructor_EdgeKindIsConstructor()
    {
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "CallGraphSamples.PropertyAndCtor", "callees", 1, 500)!;

        var rootKey = result.Callees.Keys.First(k => k.Contains("PropertyAndCtor", StringComparison.Ordinal));
        Assert.Contains(result.Callees[rootKey].Edges,
            e => e.EdgeKind == CallGraphEdgeKind.Constructor &&
                 e.Target.Contains("SampleHolder", StringComparison.Ordinal));
    }

    [Fact]
    public void Callees_PropertyAccessors_EdgeKindsAreGetAndSet()
    {
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "CallGraphSamples.PropertyAndCtor", "callees", 1, 500)!;

        var rootKey = result.Callees.Keys.First(k => k.Contains("PropertyAndCtor", StringComparison.Ordinal));
        var edges = result.Callees[rootKey].Edges;
        Assert.Contains(edges, e => e.EdgeKind == CallGraphEdgeKind.PropertyGet);
        Assert.Contains(edges, e => e.EdgeKind == CallGraphEdgeKind.PropertySet);
    }

    [Fact]
    public void Callees_OperatorOverload_EdgeKindIsOperator()
    {
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "CallGraphSamples.UseOperator", "callees", 1, 500)!;

        var rootKey = result.Callees.Keys.First(k => k.Contains("UseOperator", StringComparison.Ordinal));
        Assert.Contains(result.Callees[rootKey].Edges,
            e => e.EdgeKind == CallGraphEdgeKind.Operator);
    }

    [Fact]
    public void Callees_Truncation_SetsFlagAndStopsExpanding()
    {
        // Tiny cap forces truncation early; partial result should still be useful.
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "CallGraphSamples.Root", "callees", 5, 2)!;

        Assert.True(result.Truncated);
        Assert.True(result.Callees.Count <= 2);
    }

    [Fact]
    public void Callers_Greet_FindsOldGreetCaller()
    {
        // OldGreet calls Greet — Greet's callers must include OldGreet.
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "Greeter.Greet", "callers", 3, 500)!;

        Assert.Equal("callers", result.Direction);
        Assert.Empty(result.Callees);
        Assert.NotEmpty(result.Callers);

        // OldGreet must appear somewhere in the callers map (either as a key or as an edge target).
        var oldGreetSomewhere =
            result.Callers.Keys.Any(k => k.Contains("OldGreet", StringComparison.Ordinal)) ||
            result.Callers.Values.Any(n => n.Edges.Any(e => e.Target.Contains("OldGreet", StringComparison.Ordinal)));
        Assert.True(oldGreetSomewhere);
    }

    [Fact]
    public void Both_Direction_PopulatesBothMaps()
    {
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "Greeter.Greet", "both", 2, 500)!;

        Assert.Equal("both", result.Direction);
        Assert.NotEmpty(result.Callees);
        Assert.NotEmpty(result.Callers);
    }

    [Fact]
    public void RootNode_HasMethodKindAndProjectInfo()
    {
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "CallGraphSamples.Root", "callees", 1, 500)!;

        var root = result.Callees[result.Root];
        Assert.False(root.IsExternal);
        Assert.Equal("TestLib", root.Project);
        Assert.NotEmpty(root.FilePath);
        Assert.True(root.Line > 0);
        Assert.Equal(CallGraphNodeKind.Method, root.Kind);
    }

    [Fact]
    public void InvalidDirection_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            GetCallGraphLogic.Execute(
                _loaded, _resolver, _metadata, "CallGraphSamples.Root", "sideways", 3, 500));
    }
}
```

**Step 2: Run to verify they fail (compile error)**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetCallGraphToolTests"
```

Expected: compile error — `GetCallGraphLogic` doesn't exist.

**Step 3: Create `src/RoslynCodeLens/Tools/GetCallGraphLogic.cs`**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class GetCallGraphLogic
{
    private static readonly SymbolDisplayFormat FqnFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMemberOptions(
            SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeExplicitInterface)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType);

    public static GetCallGraphResult? Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        MetadataSymbolResolver metadata,
        string symbol,
        string direction,
        int maxDepth,
        int maxNodes)
    {
        if (direction is not ("callees" or "callers" or "both"))
            throw new ArgumentException(
                $"Invalid direction '{direction}'. Expected 'callees', 'callers', or 'both'.",
                nameof(direction));

        var methods = resolver.FindMethods(symbol);
        if (methods.Count == 0) return null;

        var root = methods[0];
        var rootFqn = Fqn(root);

        var callees = new Dictionary<string, CallGraphNode>(StringComparer.Ordinal);
        var callers = new Dictionary<string, CallGraphNode>(StringComparer.Ordinal);
        var truncated = false;

        if (direction is "callees" or "both")
            truncated |= WalkCallees(loaded, resolver, root, rootFqn, maxDepth, maxNodes, callees);

        if (direction is "callers" or "both")
            truncated |= WalkCallers(loaded, resolver, root, rootFqn, maxDepth, maxNodes, callers);

        return new GetCallGraphResult(
            Root: rootFqn,
            Direction: direction,
            MaxDepthRequested: maxDepth,
            Truncated: truncated,
            Callees: callees,
            Callers: callers);
    }

    private static bool WalkCallees(
        LoadedSolution loaded,
        SymbolResolver resolver,
        IMethodSymbol root,
        string rootFqn,
        int maxDepth,
        int maxNodes,
        Dictionary<string, CallGraphNode> map)
    {
        var queue = new Queue<(IMethodSymbol Sym, int Depth)>();
        queue.Enqueue((root, 0));
        map[rootFqn] = BuildNode(resolver, root, edges: []);

        var truncated = false;

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            var currentFqn = Fqn(current);
            var edges = new List<CallGraphEdge>();

            foreach (var (callee, edgeKind) in EnumerateOutgoingCalls(loaded, current))
            {
                var calleeFqn = Fqn(callee);

                if (!map.ContainsKey(calleeFqn))
                {
                    if (map.Count >= maxNodes)
                    {
                        truncated = true;
                        continue;
                    }

                    var isExternal = !callee.Locations.Any(l => l.IsInSource);
                    map[calleeFqn] = BuildNode(resolver, callee, edges: [], forceExternal: isExternal);

                    if (!isExternal)
                        queue.Enqueue((callee, depth + 1));
                }

                edges.Add(new CallGraphEdge(calleeFqn, edgeKind));
            }

            map[currentFqn] = map[currentFqn] with { Edges = edges };
        }

        return truncated;
    }

    private static bool WalkCallers(
        LoadedSolution loaded,
        SymbolResolver resolver,
        IMethodSymbol root,
        string rootFqn,
        int maxDepth,
        int maxNodes,
        Dictionary<string, CallGraphNode> map)
    {
        var queue = new Queue<(IMethodSymbol Sym, int Depth)>();
        queue.Enqueue((root, 0));
        map[rootFqn] = BuildNode(resolver, root, edges: []);

        var truncated = false;

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            var currentFqn = Fqn(current);
            var edges = new List<CallGraphEdge>();

            // FindCallersAsync returns the methods that call `current`. Each is a "caller" edge.
            var callerInfos = SymbolFinder.FindCallersAsync(current, loaded.Solution)
                .GetAwaiter().GetResult();

            foreach (var info in callerInfos)
            {
                if (info.CallingSymbol is not IMethodSymbol caller) continue;

                var callerFqn = Fqn(caller);
                if (!map.ContainsKey(callerFqn))
                {
                    if (map.Count >= maxNodes)
                    {
                        truncated = true;
                        continue;
                    }

                    map[callerFqn] = BuildNode(resolver, caller, edges: []);
                    queue.Enqueue((caller, depth + 1));
                }

                edges.Add(new CallGraphEdge(callerFqn, EdgeKindFor(caller)));
            }

            map[currentFqn] = map[currentFqn] with { Edges = edges };
        }

        return truncated;
    }

    private static IEnumerable<(IMethodSymbol Callee, CallGraphEdgeKind EdgeKind)> EnumerateOutgoingCalls(
        LoadedSolution loaded, IMethodSymbol method)
    {
        var location = method.Locations.FirstOrDefault(l => l.IsInSource);
        if (location?.SourceTree is null) yield break;

        Compilation? compilation = null;
        foreach (var (_, comp) in loaded.Compilations)
        {
            if (comp.SyntaxTrees.Contains(location.SourceTree))
            {
                compilation = comp;
                break;
            }
        }
        if (compilation is null) yield break;

        var semanticModel = compilation.GetSemanticModel(location.SourceTree);
        var bodyNode = location.SourceTree.GetRoot().FindNode(location.SourceSpan);

        var seen = new HashSet<(string, CallGraphEdgeKind)>(EqualityComparer<(string, CallGraphEdgeKind)>.Default);

        foreach (var node in bodyNode.DescendantNodesAndSelf())
        {
            IMethodSymbol? called = null;
            CallGraphEdgeKind kind = CallGraphEdgeKind.Method;

            switch (node)
            {
                case InvocationExpressionSyntax inv:
                    if (semanticModel.GetSymbolInfo(inv).Symbol is IMethodSymbol m)
                    {
                        called = m;
                        kind = m.MethodKind == MethodKind.UserDefinedOperator
                            ? CallGraphEdgeKind.Operator
                            : CallGraphEdgeKind.Method;
                    }
                    break;

                case ObjectCreationExpressionSyntax oc:
                    if (semanticModel.GetSymbolInfo(oc).Symbol is IMethodSymbol ctor)
                    {
                        called = ctor;
                        kind = CallGraphEdgeKind.Constructor;
                    }
                    break;

                case AssignmentExpressionSyntax asg:
                    if (semanticModel.GetSymbolInfo(asg.Left).Symbol is IPropertySymbol setProp
                        && setProp.SetMethod is IMethodSymbol setter)
                    {
                        called = setter;
                        kind = CallGraphEdgeKind.PropertySet;
                    }
                    break;

                case MemberAccessExpressionSyntax ma:
                    if (semanticModel.GetSymbolInfo(ma).Symbol is IPropertySymbol getProp
                        && !IsPropertyWriteContext(ma)
                        && getProp.GetMethod is IMethodSymbol getter)
                    {
                        called = getter;
                        kind = CallGraphEdgeKind.PropertyGet;
                    }
                    break;

                case BinaryExpressionSyntax bin:
                    if (semanticModel.GetSymbolInfo(bin).Symbol is IMethodSymbol op
                        && op.MethodKind == MethodKind.UserDefinedOperator)
                    {
                        called = op;
                        kind = CallGraphEdgeKind.Operator;
                    }
                    break;
            }

            if (called is null) continue;
            var dedupKey = (Fqn(called), kind);
            if (!seen.Add(dedupKey)) continue;
            yield return (called, kind);
        }
    }

    private static bool IsPropertyWriteContext(MemberAccessExpressionSyntax ma)
        => ma.Parent is AssignmentExpressionSyntax asg && asg.Left == ma;

    private static CallGraphNode BuildNode(
        SymbolResolver resolver,
        IMethodSymbol symbol,
        IReadOnlyList<CallGraphEdge> edges,
        bool forceExternal = false)
    {
        var isExternal = forceExternal || !symbol.Locations.Any(l => l.IsInSource);
        var (file, line) = isExternal ? ("", 0) : resolver.GetFileAndLine(symbol);
        var project = isExternal ? "" : resolver.GetProjectName(symbol);

        return new CallGraphNode(
            Kind: NodeKindFor(symbol),
            Project: project,
            FilePath: file,
            Line: line,
            IsExternal: isExternal,
            Edges: edges);
    }

    private static CallGraphNodeKind NodeKindFor(IMethodSymbol symbol)
        => symbol.MethodKind switch
        {
            MethodKind.Constructor => CallGraphNodeKind.Constructor,
            MethodKind.UserDefinedOperator or MethodKind.Conversion => CallGraphNodeKind.Operator,
            MethodKind.PropertyGet or MethodKind.PropertySet => CallGraphNodeKind.Property,
            _ => CallGraphNodeKind.Method
        };

    private static CallGraphEdgeKind EdgeKindFor(IMethodSymbol symbol)
        => symbol.MethodKind switch
        {
            MethodKind.Constructor => CallGraphEdgeKind.Constructor,
            MethodKind.UserDefinedOperator or MethodKind.Conversion => CallGraphEdgeKind.Operator,
            MethodKind.PropertyGet => CallGraphEdgeKind.PropertyGet,
            MethodKind.PropertySet => CallGraphEdgeKind.PropertySet,
            _ => CallGraphEdgeKind.Method
        };

    private static string Fqn(ISymbol symbol)
        => symbol.ToDisplayString(FqnFormat).Replace("global::", string.Empty, StringComparison.Ordinal);
}
```

**Step 4: Run the tests**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetCallGraphToolTests" -v normal
```

Expected: 14/14 pass.

**Common debugging:**
- If `Cycle_BothNodesPresent` fails: the visited check (`!map.ContainsKey(calleeFqn)`) must precede enqueue. Re-visiting an already-mapped node should still add the edge but not re-enqueue.
- If `Truncation` fails: check that `map.Count >= maxNodes` is checked BEFORE adding to the dict, and that `truncated = true` is set when skipping.
- If `Callers_FindsOldGreet` fails: `SymbolFinder.FindCallersAsync` is async; ensure `.GetAwaiter().GetResult()` (or pass through `Task.Run`) is used. Don't add `.Result` directly — that's a sync-over-async violation our own `find_async_violations` would flag.
- If property accessor edges miss: `MemberAccessExpressionSyntax` resolution on the LEFT side of an assignment is the setter; everywhere else it's the getter. The `IsPropertyWriteContext` helper handles that.

**Step 5: Run full suite**

```bash
dotnet test
```

Expected: pre-existing flaky failures only (the metadata-resolution tests on Windows). New tests all green.

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Tools/GetCallGraphLogic.cs \
  tests/RoslynCodeLens.Tests/Tools/GetCallGraphToolTests.cs
git commit -m "feat: add GetCallGraphLogic with transitive caller/callee BFS"
```

---

## Task 4: `GetCallGraphTool` MCP wrapper

Thin wrapper, auto-registered via `WithToolsFromAssembly()`.

**Files:**
- Create: `src/RoslynCodeLens/Tools/GetCallGraphTool.cs`

**Step 1: Create the wrapper**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetCallGraphTool
{
    [McpServerTool(Name = "get_call_graph")]
    [Description(
        "Transitive caller and/or callee graph for a method symbol, depth-bounded with cycle " +
        "detection. Output is an adjacency-list dict per direction (callees and/or callers), " +
        "where each visited symbol maps to its outgoing edges. " +
        "Direction: 'callees' (what this method transitively calls), 'callers' (who reaches " +
        "this method), or 'both'. " +
        "External (BCL/NuGet) callees appear as terminal leaves with isExternal=true. " +
        "Declared signature only on callee side — no virtual dispatch resolution; agent uses " +
        "find_implementations separately if needed. Callers side resolves dispatch naturally " +
        "via Roslyn SymbolFinder. " +
        "Hard cap on total visited nodes (default 500) — sets truncated=true if hit. " +
        "Use this instead of recursive find_callers / analyze_method calls when you need " +
        "depth > 1.")]
    public static GetCallGraphResult? Execute(
        MultiSolutionManager manager,
        [Description("Method symbol (e.g. 'Greeter.Greet' or 'MyNamespace.MyClass.MyMethod')")]
        string symbol,
        [Description("'callees' (default), 'callers', or 'both'.")]
        string direction = "callees",
        [Description("Max traversal depth from root. Default 3.")]
        int maxDepth = 3,
        [Description("Hard cap on total visited nodes. Default 500.")]
        int maxNodes = 500)
    {
        manager.EnsureLoaded();
        return GetCallGraphLogic.Execute(
            manager.GetLoadedSolution(),
            manager.GetResolver(),
            manager.GetMetadataResolver(),
            symbol,
            direction,
            maxDepth,
            maxNodes);
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
dotnet test tests/RoslynCodeLens.Tests --filter "GetCallGraphToolTests" -v normal
```

Expected: 14/14 pass (still — the wrapper doesn't change Logic behavior).

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Tools/GetCallGraphTool.cs
git commit -m "feat: register get_call_graph MCP tool"
```

---

## Task 5: Add benchmark

Two benchmarks: callees and callers direction at depth 3.

**Files:**
- Modify: `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

**Step 1: Read the file** and find the existing `analyze_method` benchmark — add the new benchmarks immediately after.

**Step 2: Add the benchmark methods**

```csharp
[Benchmark(Description = "get_call_graph: Greet (callees, depth 3)")]
public object GetCallGraphCallees()
{
    return GetCallGraphLogic.Execute(
        _loaded, _resolver, _metadata, "Greeter.Greet", "callees", 3, 500)!;
}

[Benchmark(Description = "get_call_graph: Greet (callers, depth 3)")]
public object GetCallGraphCallers()
{
    return GetCallGraphLogic.Execute(
        _loaded, _resolver, _metadata, "Greeter.Greet", "callers", 3, 500)!;
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
git commit -m "bench: add get_call_graph benchmarks"
```

---

## Task 6: Update SKILL.md, README, CLAUDE.md

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
- Modify: `README.md`
- Modify: `CLAUDE.md`

**Step 1: SKILL.md — Red Flags routing table**

Add near the existing `find_callers` / `analyze_method` entries:

```
| "What does this method end up calling?" / "Show me the transitive callees" / "What's the blast radius for changing X?" | `get_call_graph` |
```

**Step 2: SKILL.md — Quick Reference table**

Add near `analyze_method`:

```
| `get_call_graph` | "Transitive callers/callees, depth-bounded" |
```

**Step 3: SKILL.md — Navigating Code section**

Add as a new bullet near `find_callers` and `analyze_method`:

```
- `get_call_graph` — Transitive caller/callee graph for a method, depth-bounded with cycle detection. Adjacency-list output. Use when you need depth > 1 (`analyze_method` is depth=1).
```

**Step 4: README.md Features list**

Add near `analyze_method`:

```
- **get_call_graph** — Transitive caller/callee graph for a method, depth-bounded with cycle detection.
```

**Step 5: CLAUDE.md — bump tool count**

Change "28 code intelligence tools" to "29 code intelligence tools".

**Step 6: Sanity check**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetCallGraphToolTests" -v normal
```

Expected: 14/14 pass.

**Step 7: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md \
  README.md \
  CLAUDE.md
git commit -m "docs: announce get_call_graph in SKILL.md, README, CLAUDE.md"
```

---

## Done

After Task 6 the branch should have ~8 commits (design + plan + 6 implementation tasks), all `GetCallGraphToolTests` green, the benchmark project compiling, and the tool auto-registered. From there: `/superpowers:requesting-code-review` → PR.
