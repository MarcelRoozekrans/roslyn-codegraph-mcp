# `find_event_subscribers` Design

**Status:** Approved 2026-04-29

## Goal

Given an event symbol (source or metadata), find every `+=` and `-=` site across the solution. Each site reports its source location, the resolved handler, and whether it's a subscribe or unsubscribe.

## Use cases

- "Who's listening to `Button.Click`?" — UI event subscriber audit.
- "Who registers for `IMessageBus.MessageReceived`?" — interface-event dispatch.
- "Find subscribed-but-not-unsubscribed pairs for `LongLivedService.DataChanged`" — memory-leak hunt; caller diffs subscribe vs. unsubscribe sites client-side.

## API

```csharp
IReadOnlyList<EventSubscriberInfo> Execute(
    string symbol  // e.g. "MyClass.Clicked" or "System.Diagnostics.Process.Exited"
)
```

## Output

```csharp
public record EventSubscriberInfo(
    string EventName,         // fully-qualified event name
    string HandlerName,       // method FQN, or "<lambda at File.cs:42>"
    SubscriptionKind Kind,    // Subscribe | Unsubscribe
    string FilePath,
    int Line,
    string Snippet,           // the += / -= line text
    string Project,
    bool IsGenerated);

public enum SubscriptionKind { Subscribe, Unsubscribe }
```

Flat list, sorted by `(FilePath, Line)` ordinal. Mirrors `CallerInfo` shape from `FindCallersLogic`.

## Architecture

### Symbol resolution

1. Source path: extend `SymbolResolver` with a `FindEvents(string symbol)` method (the existing `FindMethods` only walks methods). Returns `IReadOnlyList<IEventSymbol>`.
2. Metadata fallback: when source resolution returns empty, call `MetadataSymbolResolver.Resolve(symbol)` and accept the result if it's an `IEventSymbol`. Same pattern as `FindCallersLogic.cs:13-28`.
3. Build a target set (`HashSet<IEventSymbol>` with `SymbolEqualityComparer.Default`) plus a `targetMetadataKeys` set keyed by `{ContainingTypeFqn}.{EventName}` for cross-compilation metadata matches.

### Walk

For each compilation's syntax trees, find `AssignmentExpressionSyntax` where `node.Kind() is AddAssignmentExpression or SubtractAssignmentExpression`.

### Match

Resolve `asg.Left` via `SemanticModel.GetSymbolInfo` to `IEventSymbol`. Compare against the target set:
- Direct equality (`SymbolEqualityComparer.Default`).
- `OriginalDefinition` equality (for generic instantiations).
- Cross-compilation metadata fallback (compare by `{ContainingTypeFqn}.{EventName}`).
- Interface-implementation fallback: if target is an interface event and the LHS resolves to an implementation, accept. Mirrors `FindCallersLogic.IsInterfaceImplementation`.

### Handler resolution

Examine `asg.Right`:

| Right-hand side | Handler representation |
|---|---|
| `IdentifierNameSyntax` (method group) → resolves to `IMethodSymbol` | `IMethodSymbol.ToDisplayString()` (FQN with parameter types) |
| `MemberAccessExpressionSyntax` (qualified method group) → `IMethodSymbol` | Same |
| `LambdaExpressionSyntax` | `<lambda at {FilePath}:{Line}>` |
| `AnonymousMethodExpressionSyntax` (`delegate (s, e) { ... }`) | `<anonymous-method at {FilePath}:{Line}>` |
| Anything else (rare: indexer return, ternary, method invocation returning a delegate) | `<expression at {FilePath}:{Line}>` |

### Build entry

- `FilePath`, `Line` — from `asg.GetLocation().GetLineSpan()`.
- `Snippet` — `asg.ToString()` (single-line statement-level snippet).
- `Project` — from the iterating compilation's project.
- `IsGenerated` — `SymbolResolver.IsGenerated(file)`.
- Dedup by `(file, span)` (not `(file, line)`) so two `+=` on the same line both register.

## Edge cases

| Case | Handling |
|---|---|
| Interface event with multiple implementations | Match all impls via `IsInterfaceImplementation` fallback |
| Generic event types / instantiations | `SymbolEqualityComparer.Default` matches `OriginalDefinition`; works |
| Cross-compilation metadata symbols | Name-based fallback (same as `FindCallersLogic.BuildMetadataKeys`) |
| Test projects | Included (consistent with `find_callers`) |
| Generated code | Included with `IsGenerated: true` flag |
| `obj.E += a; obj.E += b;` on same line | Both entries — dedup by `(file, span)` not `(file, line)` |
| `event` declaration with explicit `add`/`remove` accessors | Source resolution finds the declaration; subscriptions are still `+=` syntax |

## MCP wrapper

Standard pattern matching `find_callers`:

```csharp
[McpServerToolType]
public static class FindEventSubscribersTool
{
    [McpServerTool(Name = "find_event_subscribers")]
    [Description(...)]
    public static IReadOnlyList<EventSubscriberInfo> Execute(
        MultiSolutionManager manager,
        string symbol)
    { ... }
}
```

Auto-registered via `WithToolsFromAssembly()` — no `Program.cs` edit.

## Testing

Fixture additions to `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/`:
- `EventSubscriberSamples.cs` — class declaring an event, multiple subscribers across method-group / lambda / anonymous-method patterns, a subscribe+unsubscribe pair, an interface event with two implementations.

Tests:

| Test | Asserts |
|---|---|
| `Subscribe_MethodGroup_ReportsHandlerFqn` | Handler name is the method's FQN |
| `Subscribe_Lambda_ReportsSyntheticName` | Handler name starts with `<lambda` |
| `Subscribe_AnonymousMethod_ReportsSyntheticName` | Handler name starts with `<anonymous-method` |
| `Unsubscribe_TaggedAsUnsubscribe` | `Kind == Unsubscribe` for `-=` sites |
| `ExternalEvent_FullyQualified_FindsSubscriptions` | Subscriber to e.g. `Console.CancelKeyPress` is found via FQN |
| `InterfaceEvent_MatchesImplementations` | Subscribers via the implementation type are still found when querying the interface event |
| `UnknownSymbol_ReturnsEmpty` | Empty list, no exception |
| `Result_SortedByFileLine` | Sort invariant holds |
| `Result_HasGeneratedFlag` | A subscription in a generated file gets `IsGenerated: true` |

## Performance

Benchmark added: `find_event_subscribers: known event`. Expected to be similar to `find_callers` — same per-tree syntax walk + semantic resolution.

## Out of scope (deferred)

- Static-analysis leak detection (subscribed-but-not-unsubscribed): caller can compute client-side from result.
- Reflection-based subscriptions (`event.AddEventHandler(target, delegate)`).
- Source-generator-emitted subscriptions only appear if the generated code is loaded — same as other tools.

## File checklist

- `src/RoslynCodeLens/Tools/FindEventSubscribersLogic.cs`
- `src/RoslynCodeLens/Tools/FindEventSubscribersTool.cs`
- `src/RoslynCodeLens/Models/EventSubscriberInfo.cs`
- `src/RoslynCodeLens/Models/SubscriptionKind.cs`
- `src/RoslynCodeLens/Symbols/SymbolResolver.cs` — add `FindEvents` method
- `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/EventSubscriberSamples.cs`
- `tests/RoslynCodeLens.Tests/Tools/FindEventSubscribersToolTests.cs`
- `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs` — add benchmark
- `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` — Red Flags, Quick Reference, Navigating Code
- `README.md` — Features list
- `CLAUDE.md` — bump tool count
