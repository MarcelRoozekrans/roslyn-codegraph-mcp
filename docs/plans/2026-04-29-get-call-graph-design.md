# `get_call_graph` Design

**Status:** Approved 2026-04-29

## Goal

Standalone MCP tool that returns the transitive caller and/or callee graph of a symbol, depth-bounded with cycle detection. Generalises what `analyze_method` does at depth=1.

## Use cases

- "What does `Process()` end up calling?" — callees direction, see all transitive effects before refactoring.
- "Who reaches `IMessageBus.Publish`?" — callers direction, blast-radius analysis.
- "Show me the full neighborhood around this method" — both directions, escape hatch for full picture.

## API

```csharp
GetCallGraphResult Execute(
    string symbol,                 // e.g. "Greeter.Greet"
    string direction = "callees",  // "callers" | "callees" | "both"
    int maxDepth = 3,
    int maxNodes = 500
)
```

## Output shape (adjacency list)

```jsonc
{
  "root": "TestLib.Greeter.Greet(string)",
  "direction": "callees",
  "maxDepthRequested": 3,
  "truncated": false,
  "callees": {
    "TestLib.Greeter.Greet(string)": {
      "kind": "Method",
      "project": "TestLib",
      "filePath": "src/TestLib/Greeter.cs",
      "line": 8,
      "isExternal": false,
      "edges": [
        { "target": "System.String.Format(...)", "edgeKind": "Method" },
        { "target": "TestLib.Greeter..ctor(...)", "edgeKind": "Constructor" }
      ]
    },
    "System.String.Format(...)": {
      "kind": "Method",
      "project": "",
      "filePath": "",
      "line": 0,
      "isExternal": true,
      "edges": []
    }
  },
  "callers": {}
}
```

Cycles surface naturally — a node points back at one already in the dict, and we don't re-expand.

### Why adjacency list (not flat list with paths, not nested tree)

- **Flat with paths**: duplicates entries on diamond graphs (B reached via A1 and A2 → two rows).
- **Nested tree**: explodes JSON size, breaks on cycles, hard to consume programmatically.
- **Adjacency**: compact, matches Roslyn's symbol graph natively, agent can derive paths/depth client-side.

## Architecture

### Callees direction (BFS)

1. Resolve `symbol` via `SymbolResolver.FindMethods()` — take first match (consistent with `analyze_method`).
2. For each visited source-located node:
   - Walk method body for `InvocationExpressionSyntax`, `ObjectCreationExpressionSyntax`, member-access expressions on properties/indexers/operators.
   - Resolve each via `SemanticModel.GetSymbolInfo` to `IMethodSymbol`.
   - Add edge `current → callee`.
   - If callee has source location and not yet visited, enqueue.
   - If callee has no source location (metadata), add as terminal leaf (`isExternal: true`, `edges: []`) and don't recurse.
3. Declared signature only — no virtual dispatch resolution. Agent uses `find_implementations` separately if needed.
4. Calls inside lambdas / local functions are followed (consistent with `AnalyzeMethodLogic.FindOutgoingCalls`).

### Callers direction (BFS)

1. Same root resolution.
2. For each visited node, call `SymbolFinder.FindCallersAsync` (handles virtual dispatch naturally — finds all callers across implementations).
3. Each `CallerInfo` becomes a reverse edge.
4. External callers don't apply (callers must be in source — Roslyn's `SymbolFinder` only walks the loaded compilations).

### Both direction

Run callees and callers passes independently. Populate both `callees` and `callers` maps in the result.

### Cycle detection

Visited set keyed by FQN (`IMethodSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)` minus `global::`). Reaching an already-visited node creates an edge but doesn't re-recurse.

### Truncation

When total node count (sum across both maps) hits `maxNodes`, stop adding new nodes and set `truncated: true`. Partial result preserved — agent can re-query with smaller `maxDepth` or a more specific symbol.

### Edge kinds

Derived from `IMethodSymbol.MethodKind` and syntax context:

| EdgeKind | Source |
|---|---|
| `Method` | Ordinary method invocation |
| `PropertyGet` | `obj.Foo` (read) |
| `PropertySet` | `obj.Foo = x` |
| `Constructor` | `new Foo(...)` |
| `Operator` | User-defined operator overload |

Indexer access (`obj[i]`) maps to `PropertyGet` / `PropertySet`. Lets agents filter client-side without needing separate kinds.

## Scope decisions

| Concern | Decision |
|---|---|
| Test projects | Included if reachable (consistent with `find_callers`). |
| Generated code | Included. |
| Multiple overloads of input symbol | First match wins (consistent with `analyze_method`). |
| Virtual/interface dispatch on callees | Declared signature only. |
| Virtual/interface dispatch on callers | Resolved via `SymbolFinder` (gives all dispatch sites naturally). |
| External (BCL/NuGet) calls | Included as terminal leaves on callees side; not applicable on callers side. |

## Models

```csharp
public record GetCallGraphResult(
    string Root,
    string Direction,
    int MaxDepthRequested,
    bool Truncated,
    IReadOnlyDictionary<string, CallGraphNode> Callees,
    IReadOnlyDictionary<string, CallGraphNode> Callers);

public record CallGraphNode(
    CallGraphNodeKind Kind,
    string Project,
    string FilePath,
    int Line,
    bool IsExternal,
    IReadOnlyList<CallGraphEdge> Edges);

public record CallGraphEdge(
    string Target,
    CallGraphEdgeKind EdgeKind);

public enum CallGraphNodeKind { Method, Property, Constructor, Operator }
public enum CallGraphEdgeKind { Method, PropertyGet, PropertySet, Constructor, Operator }
```

## Testing

Mirrors `AnalyzeMethodToolTests` plus transitive cases:

- Direct call → depth-1 edge present
- 3-level chain (A → B → C) → all three nodes in the dict
- Cycle (A → B → A) → both nodes present, edges close the loop
- External call (e.g. `string.Format`) → leaf node, `isExternal: true`, empty edges
- Truncation — graph against a fanout-heavy symbol with `maxNodes: 5` → `truncated: true`, partial dict
- Direction `"callers"` against `Greet` → callers map populated, callees empty
- Direction `"both"` → both maps independently populated
- Constructor edge — calling `new Foo()` produces a `Constructor` edge
- Property accessor edge — reading a property produces `PropertyGet`, writing produces `PropertySet`
- maxDepth bound — depth-4 method chain queried with `maxDepth: 2` → only 2 levels appear

## Performance

Benchmark added to `CodeGraphBenchmarks` for `get_call_graph: Greet (callees, depth 3)` and `(callers, depth 3)`. Expect comparable timing to `find_callers` for callers direction; callees should be faster (no SymbolFinder, just syntax walks).

## Out of scope (deferred)

- Edge-level annotations (call site location for each edge — could expand the JSON significantly)
- Direction-aware path computation server-side
- Method-group expressions (`Action a = obj.Method;`) — only direct invocations are followed
- Async state-machine awaits as separate edge kind

## File checklist

- `src/RoslynCodeLens/Tools/GetCallGraphLogic.cs`
- `src/RoslynCodeLens/Tools/GetCallGraphTool.cs`
- `src/RoslynCodeLens/Models/GetCallGraphResult.cs`
- `src/RoslynCodeLens/Models/CallGraphNode.cs`
- `src/RoslynCodeLens/Models/CallGraphEdge.cs`
- `src/RoslynCodeLens/Models/CallGraphNodeKind.cs`
- `src/RoslynCodeLens/Models/CallGraphEdgeKind.cs`
- `tests/RoslynCodeLens.Tests/Tools/GetCallGraphToolTests.cs`
- `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs` — add benchmark
- `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` — Red Flags, Quick Reference, Understanding-a-Codebase
- `README.md` — Features list
- `CLAUDE.md` — bump tool count
