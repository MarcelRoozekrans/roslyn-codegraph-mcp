# `find_event_subscribers` Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a new MCP tool `find_event_subscribers` that finds every `+=` and `-=` site for a given event symbol (source or metadata). Each result reports source location, resolved handler, and subscription kind.

**Architecture:** Walk all syntax trees for `AssignmentExpressionSyntax` whose kind is `AddAssignmentExpression` or `SubtractAssignmentExpression`. Resolve LHS to `IEventSymbol`, match against target event (with cross-compilation metadata + interface-implementation fallbacks). Resolve RHS handler — method group → `IMethodSymbol` FQN, lambda/anonymous → synthesized `<lambda at File.cs:N>`. Mirrors `FindCallersLogic` for matching strategy.

**Tech Stack:** Roslyn (`Microsoft.CodeAnalysis`, `Microsoft.CodeAnalysis.CSharp.Syntax`), xUnit, BenchmarkDotNet, ModelContextProtocol.Server.

**Reference design:** `docs/plans/2026-04-29-find-event-subscribers-design.md`

**Patterns to mirror (read these before starting):**
- Tool wrapper: `src/RoslynCodeLens/Tools/FindCallersTool.cs`
- Logic class with metadata-fallback + interface-impl matching: `src/RoslynCodeLens/Tools/FindCallersLogic.cs`
- `SymbolResolver.FindMethods`: `src/RoslynCodeLens/SymbolResolver.cs:215-230` (mirror this for `FindEvents`)
- Existing event handling for inspiration: `src/RoslynCodeLens/Tools/PublicApiSurfaceExtractor.cs:252` (`IEventSymbol` case)
- MCP auto-registration: `src/RoslynCodeLens/Program.cs:35` uses `WithToolsFromAssembly()` — no `Program.cs` edit needed

---

## Task 1: Add `SymbolResolver.FindEvents`

**Files:**
- Modify: `src/RoslynCodeLens/SymbolResolver.cs`

**Step 1: Add the method** immediately after `FindMethods`:

```csharp
public IReadOnlyList<IEventSymbol> FindEvents(string symbol)
{
    var results = new List<IEventSymbol>();
    var parts = symbol.Split('.');
    if (parts.Length < 2) return results;

    var typeName = string.Join('.', parts[..^1]);
    var eventName = parts[^1];

    foreach (var type in FindNamedTypes(typeName))
    {
        results.AddRange(type.GetMembers(eventName).OfType<IEventSymbol>());
    }

    return results;
}
```

**Step 2: Build to verify**

```bash
dotnet build src/RoslynCodeLens
```

Expected: 0 errors.

**Step 3: Commit**

```bash
git add src/RoslynCodeLens/SymbolResolver.cs
git commit -m "feat: add SymbolResolver.FindEvents helper"
```

---

## Task 2: Models

Two files: enum + record.

**Files:**
- Create: `src/RoslynCodeLens/Models/SubscriptionKind.cs`
- Create: `src/RoslynCodeLens/Models/EventSubscriberInfo.cs`

**Step 1: `SubscriptionKind.cs`**

```csharp
namespace RoslynCodeLens.Models;

public enum SubscriptionKind
{
    Subscribe,
    Unsubscribe
}
```

**Step 2: `EventSubscriberInfo.cs`**

```csharp
namespace RoslynCodeLens.Models;

public record EventSubscriberInfo(
    string EventName,
    string HandlerName,
    SubscriptionKind Kind,
    string FilePath,
    int Line,
    string Snippet,
    string Project,
    bool IsGenerated);
```

**Step 3: Build**

```bash
dotnet build src/RoslynCodeLens
```

Expected: 0 errors.

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Models/SubscriptionKind.cs \
  src/RoslynCodeLens/Models/EventSubscriberInfo.cs
git commit -m "feat: add models for find_event_subscribers"
```

---

## Task 3: Test fixture for event subscribers

Self-contained fixture covering: method-group subscribe, lambda subscribe, anonymous-method subscribe, subscribe+unsubscribe pair, interface event with two implementations, two `+=` on the same line.

**Files:**
- Create: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/EventSubscriberSamples.cs`

**Step 1: Create the fixture**

```csharp
using System;

namespace TestLib;

public class EventPublisher
{
    public event EventHandler? Clicked;
    public event EventHandler? Clicked2;
    public void Raise() => Clicked?.Invoke(this, EventArgs.Empty);
}

public interface IBusEventPublisher
{
    event EventHandler? MessageReceived;
}

public class BusA : IBusEventPublisher
{
    public event EventHandler? MessageReceived;
}

public class BusB : IBusEventPublisher
{
    public event EventHandler? MessageReceived;
}

public class EventSubscriberSamples
{
    private readonly EventPublisher _publisher = new();
    private readonly BusA _busA = new();
    private readonly BusB _busB = new();

    public void SubscribeMethodGroup()
    {
        _publisher.Clicked += OnClicked;
    }

    public void UnsubscribeMethodGroup()
    {
        _publisher.Clicked -= OnClicked;
    }

    public void SubscribeLambda()
    {
        _publisher.Clicked += (s, e) => { };
    }

    public void SubscribeAnonymousMethod()
    {
        _publisher.Clicked += delegate (object? s, EventArgs e) { };
    }

    public void SubscribeBothBuses()
    {
        _busA.MessageReceived += OnBusMessage;
        _busB.MessageReceived += OnBusMessage;
    }

    public void TwoSubscriptionsOnSameLine()
    {
        _publisher.Clicked += OnClicked; _publisher.Clicked2 += OnClicked;
    }

    private void OnClicked(object? sender, EventArgs e) { }
    private void OnBusMessage(object? sender, EventArgs e) { }
}
```

**Step 2: Build**

```bash
dotnet build tests/RoslynCodeLens.Tests
```

Expected: 0 errors.

**Step 3: Run existing tests** (sanity — adding fixture types shouldn't break anything):

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "GetPublicApiSurfaceToolTests"
```

Expected: all green.

**Step 4: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/EventSubscriberSamples.cs
git commit -m "test: add EventSubscriberSamples fixture"
```

---

## Task 4: `FindEventSubscribersLogic` with comprehensive tests (TDD)

The walker. Source + metadata symbol resolution, interface-impl matching, handler resolution, subscribe/unsubscribe tagging, dedup by `(file, span)`.

**Files:**
- Create: `src/RoslynCodeLens/Tools/FindEventSubscribersLogic.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/FindEventSubscribersToolTests.cs`

**Step 1: Write the failing tests**

`tests/RoslynCodeLens.Tests/Tools/FindEventSubscribersToolTests.cs`:

```csharp
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindEventSubscribersToolTests : IAsyncLifetime
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
    public void UnknownSymbol_ReturnsEmpty()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "Does.Not.Exist");

        Assert.Empty(results);
    }

    [Fact]
    public void Subscribe_MethodGroup_ReportsHandlerFqn()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        var match = Assert.Single(results, r =>
            r.Kind == SubscriptionKind.Subscribe &&
            r.HandlerName.Contains("OnClicked", StringComparison.Ordinal) &&
            r.FilePath.EndsWith("EventSubscriberSamples.cs", StringComparison.Ordinal) &&
            !r.Snippet.Contains("Clicked2", StringComparison.Ordinal));

        Assert.True(match.Line > 0);
        Assert.Equal("TestLib", match.Project);
    }

    [Fact]
    public void Subscribe_Lambda_ReportsSyntheticName()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        Assert.Contains(results, r =>
            r.Kind == SubscriptionKind.Subscribe &&
            r.HandlerName.StartsWith("<lambda at ", StringComparison.Ordinal) &&
            r.HandlerName.Contains("EventSubscriberSamples.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Subscribe_AnonymousMethod_ReportsSyntheticName()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        Assert.Contains(results, r =>
            r.Kind == SubscriptionKind.Subscribe &&
            r.HandlerName.StartsWith("<anonymous-method at ", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsubscribe_TaggedAsUnsubscribe()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        Assert.Contains(results, r =>
            r.Kind == SubscriptionKind.Unsubscribe &&
            r.HandlerName.Contains("OnClicked", StringComparison.Ordinal));
    }

    [Fact]
    public void InterfaceEvent_MatchesImplementations()
    {
        // Query the interface event; subscribers via BusA/BusB should both surface.
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "IBusEventPublisher.MessageReceived");

        // Subscribers go through BusA.MessageReceived and BusB.MessageReceived (concrete impls).
        // Either we match via the interface event symbol set + implementation fallback, OR
        // the fallback reaches the impl events through `FindEvents("IBusEventPublisher.MessageReceived")`.
        // At minimum: at least 2 subscriptions found across BusA/BusB.
        Assert.True(results.Count >= 2,
            $"Expected >=2 subscribers across implementations, got {results.Count}");
    }

    [Fact]
    public void TwoSubscriptionsOnSameLine_BothReported()
    {
        var clickedResults = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");
        var clicked2Results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked2");

        // The TwoSubscriptionsOnSameLine method has BOTH events += on one source line.
        // Each event query must find its subscription in TwoSubscriptionsOnSameLine.
        Assert.Contains(clickedResults, r =>
            r.Snippet.Contains("Clicked", StringComparison.Ordinal) &&
            !r.Snippet.Contains("Clicked2", StringComparison.Ordinal));
        Assert.Contains(clicked2Results, r =>
            r.Snippet.Contains("Clicked2", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_SortedByFileLine()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        for (int i = 1; i < results.Count; i++)
        {
            var prev = results[i - 1];
            var curr = results[i];
            var fileCmp = string.CompareOrdinal(prev.FilePath, curr.FilePath);
            Assert.True(
                fileCmp < 0 || (fileCmp == 0 && prev.Line <= curr.Line),
                $"Sort violation at {i}: '{prev.FilePath}:{prev.Line}' before '{curr.FilePath}:{curr.Line}'");
        }
    }

    [Fact]
    public void EventName_IsFullyQualified()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        Assert.NotEmpty(results);
        foreach (var r in results)
        {
            Assert.Contains("Clicked", r.EventName, StringComparison.Ordinal);
            Assert.NotEmpty(r.Project);
        }
    }
}
```

**Step 2: Run to verify they fail**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindEventSubscribersToolTests"
```

Expected: compile error — `FindEventSubscribersLogic` doesn't exist.

**Step 3: Create `src/RoslynCodeLens/Tools/FindEventSubscribersLogic.cs`**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class FindEventSubscribersLogic
{
    public static IReadOnlyList<EventSubscriberInfo> Execute(
        LoadedSolution loaded, SymbolResolver source, MetadataSymbolResolver metadata, string symbol)
    {
        var targetEvents = source.FindEvents(symbol);
        if (targetEvents.Count == 0)
        {
            var resolved = metadata.Resolve(symbol);
            if (resolved?.Symbol is IEventSymbol e)
                targetEvents = [e];
            else
                return [];
        }

        var targetSet = new HashSet<IEventSymbol>(targetEvents, SymbolEqualityComparer.Default);
        var targetMetadataKeys = BuildMetadataKeys(targetEvents);
        var results = new List<EventSubscriberInfo>();
        var seen = new HashSet<(string, TextSpan)>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var projectName = source.GetProjectName(projectId);

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();

                foreach (var asg in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                {
                    var kind = asg.Kind() switch
                    {
                        SyntaxKind.AddAssignmentExpression => SubscriptionKind.Subscribe,
                        SyntaxKind.SubtractAssignmentExpression => SubscriptionKind.Unsubscribe,
                        _ => (SubscriptionKind?)null
                    };
                    if (kind is null) continue;

                    if (semanticModel.GetSymbolInfo(asg.Left).Symbol is not IEventSymbol called)
                        continue;
                    if (!IsEventMatch(called, targetSet, targetEvents, targetMetadataKeys))
                        continue;

                    if (!seen.Add((syntaxTree.FilePath, asg.Span)))
                        continue;

                    var lineSpan = asg.GetLocation().GetLineSpan();
                    var file = lineSpan.Path;
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var handler = ResolveHandlerName(asg.Right, semanticModel, file, line);

                    results.Add(new EventSubscriberInfo(
                        EventName: called.ToDisplayString(),
                        HandlerName: handler,
                        Kind: kind.Value,
                        FilePath: file,
                        Line: line,
                        Snippet: asg.ToString(),
                        Project: projectName,
                        IsGenerated: source.IsGenerated(file)));
                }
            }
        }

        results.Sort((a, b) =>
        {
            var fileCmp = string.CompareOrdinal(a.FilePath, b.FilePath);
            return fileCmp != 0 ? fileCmp : a.Line.CompareTo(b.Line);
        });

        return results;
    }

    private static HashSet<string> BuildMetadataKeys(IReadOnlyList<IEventSymbol> events)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            if (e.Locations.All(l => !l.IsInSource))
            {
                var typeName = e.ContainingType?.ToDisplayString() ?? string.Empty;
                keys.Add($"{typeName}.{e.Name}");
            }
        }
        return keys;
    }

    private static bool IsEventMatch(
        IEventSymbol called,
        HashSet<IEventSymbol> targetSet,
        IReadOnlyList<IEventSymbol> targetEvents,
        HashSet<string> targetMetadataKeys)
    {
        if (targetSet.Contains(called) || targetSet.Contains((IEventSymbol)called.OriginalDefinition))
            return true;

        if (targetMetadataKeys.Count > 0 && called.Locations.All(l => !l.IsInSource))
        {
            var typeName = (called.OriginalDefinition.ContainingType ?? called.ContainingType)
                ?.ToDisplayString() ?? string.Empty;
            if (targetMetadataKeys.Contains($"{typeName}.{called.Name}"))
                return true;
        }

        for (int i = 0; i < targetEvents.Count; i++)
        {
            if (!string.Equals(called.Name, targetEvents[i].Name, StringComparison.Ordinal))
                continue;
            if (IsInterfaceImplementation(called, targetEvents[i]))
                return true;
        }

        return false;
    }

    private static bool IsInterfaceImplementation(IEventSymbol called, IEventSymbol target)
    {
        if (target.ContainingType.TypeKind == TypeKind.Interface)
        {
            if (SymbolEqualityComparer.Default.Equals(called, target))
                return true;

            var containingType = called.ContainingType;
            var implementation = containingType.FindImplementationForInterfaceMember(target);
            if (implementation != null &&
                SymbolEqualityComparer.Default.Equals(implementation, called))
                return true;
        }

        if (called.ContainingType.TypeKind == TypeKind.Interface &&
            target.ContainingType.TypeKind == TypeKind.Interface)
        {
            return SymbolEqualityComparer.Default.Equals(
                       called.ContainingType, target.ContainingType) &&
                   string.Equals(called.Name, target.Name, StringComparison.Ordinal);
        }

        return false;
    }

    private static string ResolveHandlerName(
        ExpressionSyntax rhs, SemanticModel semanticModel, string file, int line)
    {
        switch (rhs)
        {
            case LambdaExpressionSyntax:
                return $"<lambda at {file}:{line}>";

            case AnonymousMethodExpressionSyntax:
                return $"<anonymous-method at {file}:{line}>";

            case IdentifierNameSyntax or MemberAccessExpressionSyntax:
                if (semanticModel.GetSymbolInfo(rhs).Symbol is IMethodSymbol m)
                    return m.ToDisplayString();
                break;
        }

        return $"<expression at {file}:{line}>";
    }
}
```

Note: `TextSpan` is in `Microsoft.CodeAnalysis.Text`. Add the using if the build complains.

**Step 4: Run the tests**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindEventSubscribersToolTests" -v normal
```

Expected: all 9 tests pass.

**Common debugging:**
- If `Subscribe_MethodGroup` has the wrong handler name: `IdentifierNameSyntax` resolves via `GetSymbolInfo` only when it's a method group; if the rhs is `OnClicked` (no args), `GetSymbolInfo` returns the method symbol — good.
- If `InterfaceEvent_MatchesImplementations` returns 0: check `IsInterfaceImplementation` — `BusA.MessageReceived` is an explicit re-declaration, not a `FindImplementationForInterfaceMember` hit. The fallback may need to also accept "same name + same containing-interface in the type's interface list."
- If lambda test fails because handler name is from `GetSymbolInfo`: ensure the `LambdaExpressionSyntax` case is BEFORE the generic `GetSymbolInfo` fallthrough.
- If duplicate entries appear: the dedup key `(filePath, asg.Span)` should prevent it; ensure `syntaxTree.FilePath` is non-null.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/FindEventSubscribersLogic.cs \
  tests/RoslynCodeLens.Tests/Tools/FindEventSubscribersToolTests.cs
git commit -m "feat: add FindEventSubscribersLogic with += / -= site detection"
```

---

## Task 5: `FindEventSubscribersTool` MCP wrapper

**Files:**
- Create: `src/RoslynCodeLens/Tools/FindEventSubscribersTool.cs`

**Step 1: Create the wrapper**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindEventSubscribersTool
{
    [McpServerTool(Name = "find_event_subscribers")]
    [Description(
        "Find every += and -= site for an event symbol across the solution. " +
        "Accepts source events (e.g. 'MyClass.Clicked') or metadata events " +
        "(e.g. 'System.Diagnostics.Process.Exited'). " +
        "Each result reports the source location, the resolved handler (method FQN, " +
        "or '<lambda at File.cs:N>' for inline handlers), and the subscription kind " +
        "(Subscribe for +=, Unsubscribe for -=). " +
        "Use this for memory-leak audits (compare subscribe/unsubscribe pairs), " +
        "UI event subscriber inspection, or when Grep over '+= EventName' would miss " +
        "qualified or fully-typed subscription sites. " +
        "Sort: file path ASC then line ASC.")]
    public static IReadOnlyList<EventSubscriberInfo> Execute(
        MultiSolutionManager manager,
        [Description("Event symbol (e.g. 'MyClass.Clicked' or 'System.Diagnostics.Process.Exited')")]
        string symbol)
    {
        manager.EnsureLoaded();
        return FindEventSubscribersLogic.Execute(
            manager.GetLoadedSolution(),
            manager.GetResolver(),
            manager.GetMetadataResolver(),
            symbol);
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
dotnet test tests/RoslynCodeLens.Tests --filter "FindEventSubscribersToolTests" -v normal
```

Expected: 9/9 pass.

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Tools/FindEventSubscribersTool.cs
git commit -m "feat: register find_event_subscribers MCP tool"
```

---

## Task 6: Add benchmark

**Files:**
- Modify: `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs`

**Step 1:** Find the `find_callers: IGreeter.Greet` benchmark and add the new one immediately after.

```csharp
[Benchmark(Description = "find_event_subscribers: Clicked")]
public object FindEventSubscribers()
{
    return FindEventSubscribersLogic.Execute(
        _loaded, _resolver, _metadata, "EventPublisher.Clicked");
}
```

**Step 2: Build the benchmarks project**

```bash
dotnet build benchmarks/RoslynCodeLens.Benchmarks -c Release
```

Expected: 0 errors.

**Step 3: Commit**

```bash
git add benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs
git commit -m "bench: add find_event_subscribers benchmark"
```

---

## Task 7: Update SKILL.md, README, CLAUDE.md

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
- Modify: `README.md`
- Modify: `CLAUDE.md`

**Step 1: SKILL.md — Red Flags routing table**

Add near `find_callers`:

```
| "Who subscribes to this event?" / "Find += sites" / "Are we leaking event subscriptions?" | `find_event_subscribers` |
```

**Step 2: SKILL.md — Quick Reference table**

Add near `find_callers`:

```
| `find_event_subscribers` | "Who subscribes to this event?" |
```

**Step 3: SKILL.md — Navigating Code section**

Add as a new bullet near `find_callers`:

```
- `find_event_subscribers` — Every += / -= site for an event symbol, with resolved handler name and subscribe/unsubscribe tag. Use for UI-event audits or memory-leak hunts (compare subscribe/unsubscribe pairs).
```

**Step 4: README.md Features list**

Add near `find_callers`:

```
- **find_event_subscribers** — Every += / -= site for an event symbol, with resolved handler and subscribe/unsubscribe tag.
```

**Step 5: CLAUDE.md — bump tool count**

Change "29 code intelligence tools" to "30 code intelligence tools".

**Step 6: Sanity check**

```bash
dotnet test tests/RoslynCodeLens.Tests --filter "FindEventSubscribersToolTests" -v normal
```

Expected: 9/9 pass.

**Step 7: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md \
  README.md \
  CLAUDE.md
git commit -m "docs: announce find_event_subscribers in SKILL.md, README, CLAUDE.md"
```

---

## Done

After Task 7 the branch should have ~9 commits (design + plan + 7 implementation tasks), all `FindEventSubscribersToolTests` green, the benchmark project compiling, and the tool auto-registered. From there: `/superpowers:requesting-code-review` → PR.
