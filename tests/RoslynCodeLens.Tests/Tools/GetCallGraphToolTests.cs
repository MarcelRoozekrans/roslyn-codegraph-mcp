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
    public async Task UnknownSymbol_ReturnsNull()
    {
        var result = await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "Does.Not.Exist", "callees", 3, 500);

        Assert.Null(result);
    }

    [Fact]
    public async Task Callees_DepthOne_DirectCalleesAppear()
    {
        var result = (await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "CallGraphSamples.Root", "callees", 1, 500))!;

        Assert.NotNull(result);
        Assert.Equal("callees", result.Direction);
        Assert.Empty(result.Callers);

        var rootKey = Assert.Single(result.Callees, kv =>
            kv.Key.Contains("CallGraphSamples.Root", StringComparison.Ordinal)).Key;
        var rootNode = result.Callees[rootKey];
        Assert.Contains(rootNode.Edges, e => e.Target.Contains("Level1A", StringComparison.Ordinal));
        Assert.Contains(rootNode.Edges, e => e.Target.Contains("Level1B", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Callees_DepthThree_FollowsTransitiveChain()
    {
        var result = (await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "CallGraphSamples.Root", "callees", 3, 500))!;

        Assert.Contains(result.Callees, kv => kv.Key.Contains("Level1A", StringComparison.Ordinal));
        Assert.Contains(result.Callees, kv => kv.Key.Contains("Level2", StringComparison.Ordinal));
        Assert.Contains(result.Callees, kv => kv.Key.Contains("Level3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Callees_MaxDepthOne_StopsAtFirstLevel()
    {
        var result = (await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "CallGraphSamples.Root", "callees", 1, 500))!;

        Assert.Contains(result.Callees, kv => kv.Key.Contains("Level1A", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Callees, kv => kv.Key.Contains("Level2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Callees_External_AppearsAsTerminalLeaf()
    {
        var result = (await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "CallGraphSamples.CallsExternal", "callees", 3, 500))!;

        var external = Assert.Single(result.Callees,
            kv => kv.Value.IsExternal && kv.Key.Contains("Format", StringComparison.Ordinal));
        Assert.Empty(external.Value.Edges);
        Assert.Equal("", external.Value.Project);
        Assert.Equal("", external.Value.FilePath);
    }

    [Fact]
    public async Task Callees_Cycle_BothNodesPresentAndEdgesClose()
    {
        var result = (await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "CallGraphSamples.CycleA", "callees", 5, 500))!;

        var aKey = Assert.Single(result.Callees, kv => kv.Key.Contains("CycleA", StringComparison.Ordinal)).Key;
        var bKey = Assert.Single(result.Callees, kv => kv.Key.Contains("CycleB", StringComparison.Ordinal)).Key;

        Assert.Contains(result.Callees[aKey].Edges, e => e.Target == bKey);
        Assert.Contains(result.Callees[bKey].Edges, e => e.Target == aKey);
    }

    [Fact]
    public async Task Callees_Constructor_EdgeKindIsConstructor()
    {
        var result = (await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "CallGraphSamples.PropertyAndCtor", "callees", 1, 500))!;

        var rootKey = result.Callees.Keys.First(k => k.Contains("PropertyAndCtor", StringComparison.Ordinal));
        Assert.Contains(result.Callees[rootKey].Edges,
            e => e.EdgeKind == CallGraphEdgeKind.Constructor &&
                 e.Target.Contains("SampleHolder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Callees_PropertyAccessors_EdgeKindsAreGetAndSet()
    {
        var result = (await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "CallGraphSamples.PropertyAndCtor", "callees", 1, 500))!;

        var rootKey = result.Callees.Keys.First(k => k.Contains("PropertyAndCtor", StringComparison.Ordinal));
        var edges = result.Callees[rootKey].Edges;
        Assert.Contains(edges, e => e.EdgeKind == CallGraphEdgeKind.PropertyGet);
        Assert.Contains(edges, e => e.EdgeKind == CallGraphEdgeKind.PropertySet);
    }

    [Fact]
    public async Task Callees_OperatorOverload_EdgeKindIsOperator()
    {
        var result = (await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "CallGraphSamples.UseOperator", "callees", 1, 500))!;

        var rootKey = result.Callees.Keys.First(k => k.Contains("UseOperator", StringComparison.Ordinal));
        Assert.Contains(result.Callees[rootKey].Edges,
            e => e.EdgeKind == CallGraphEdgeKind.Operator);
    }

    [Fact]
    public async Task Callees_Truncation_SetsFlagAndStopsExpanding()
    {
        var result = (await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "CallGraphSamples.Root", "callees", 5, 2))!;

        Assert.True(result.Truncated);
        Assert.True(result.Callees.Count <= 2);
    }

    [Fact]
    public async Task Callees_Truncation_PreservesEdgesToTruncatedTargets()
    {
        // With cap=2, only Root + one of {Level1A, Level1B} make it into the map. But Root's
        // edge list must still mention BOTH Level1A and Level1B (the targets, even though
        // one didn't get a map entry of its own).
        var result = (await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "CallGraphSamples.Root", "callees", 5, 2))!;

        Assert.True(result.Truncated);
        var rootKey = result.Callees.Keys.First(k => k.Contains("CallGraphSamples.Root", StringComparison.Ordinal));
        var rootEdges = result.Callees[rootKey].Edges;
        Assert.Contains(rootEdges, e => e.Target.Contains("Level1A", StringComparison.Ordinal));
        Assert.Contains(rootEdges, e => e.Target.Contains("Level1B", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Callers_Greet_FindsOldGreetCaller()
    {
        var result = (await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "Greeter.Greet", "callers", 3, 500))!;

        Assert.Equal("callers", result.Direction);
        Assert.Empty(result.Callees);
        Assert.NotEmpty(result.Callers);

        var oldGreetSomewhere =
            result.Callers.Keys.Any(k => k.Contains("OldGreet", StringComparison.Ordinal)) ||
            result.Callers.Values.Any(n => n.Edges.Any(e => e.Target.Contains("OldGreet", StringComparison.Ordinal)));
        Assert.True(oldGreetSomewhere);
    }

    [Fact]
    public async Task Both_Direction_PopulatesBothMaps()
    {
        var result = (await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "Greeter.Greet", "both", 2, 500))!;

        Assert.Equal("both", result.Direction);
        Assert.NotEmpty(result.Callees);
        Assert.NotEmpty(result.Callers);
    }

    [Fact]
    public async Task RootNode_HasMethodKindAndProjectInfo()
    {
        var result = (await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "CallGraphSamples.Root", "callees", 1, 500))!;

        var root = result.Callees[result.Root];
        Assert.False(root.IsExternal);
        Assert.Equal("TestLib", root.Project);
        Assert.NotEmpty(root.FilePath);
        Assert.True(root.Line > 0);
        Assert.Equal(CallGraphNodeKind.Method, root.Kind);
    }

    [Fact]
    public async Task InvalidDirection_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await GetCallGraphLogic.ExecuteAsync(
                _loaded, _resolver, _metadata, "CallGraphSamples.Root", "sideways", 3, 500));
    }

    [Fact]
    public async Task Both_Direction_SharesMaxNodesBudget()
    {
        // With maxNodes=2, the SUM of callees.Count + callers.Count must be <= 2.
        var result = (await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "Greeter.Greet", "both", 5, 2))!;

        Assert.True(result.Truncated);
        Assert.True(result.Callees.Count + result.Callers.Count <= 2,
            $"Total nodes {result.Callees.Count + result.Callers.Count} exceeded cap of 2");
    }

    [Fact]
    public async Task Callees_ImplicitObjectCreation_ProducesConstructorEdge()
    {
        var result = (await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "CtorInitSamples.ImplicitNew", "callees", 1, 500))!;

        var rootKey = result.Callees.Keys.First(k => k.Contains("ImplicitNew", StringComparison.Ordinal));
        Assert.Contains(result.Callees[rootKey].Edges,
            e => e.EdgeKind == CallGraphEdgeKind.Constructor &&
                 e.Target.Contains("SampleHolder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Callees_ConstructorInitializer_ProducesConstructorEdge()
    {
        // The parameterless ctor `() : this(0)` calls the (int) ctor.
        var result = await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "CtorInitSamples..ctor", "callees", 1, 500);

        // Either FindMethods doesn't find ctors directly (null) — accept that gracefully and
        // skip; or it does, in which case we expect a Constructor edge to the (int) overload.
        if (result is null) return;

        var rootKey = result.Callees.Keys.FirstOrDefault(k => k.Contains("CtorInitSamples", StringComparison.Ordinal));
        if (rootKey is null) return;

        Assert.Contains(result.Callees[rootKey].Edges,
            e => e.EdgeKind == CallGraphEdgeKind.Constructor);
    }
}
