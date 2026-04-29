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

        Assert.Contains(result.Callees, kv => kv.Key.Contains("Level1A", StringComparison.Ordinal));
        Assert.Contains(result.Callees, kv => kv.Key.Contains("Level2", StringComparison.Ordinal));
        Assert.Contains(result.Callees, kv => kv.Key.Contains("Level3", StringComparison.Ordinal));
    }

    [Fact]
    public void Callees_MaxDepthOne_StopsAtFirstLevel()
    {
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "CallGraphSamples.Root", "callees", 1, 500)!;

        Assert.Contains(result.Callees, kv => kv.Key.Contains("Level1A", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Callees, kv => kv.Key.Contains("Level2", StringComparison.Ordinal));
    }

    [Fact]
    public void Callees_External_AppearsAsTerminalLeaf()
    {
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "CallGraphSamples.CallsExternal", "callees", 3, 500)!;

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
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "CallGraphSamples.Root", "callees", 5, 2)!;

        Assert.True(result.Truncated);
        Assert.True(result.Callees.Count <= 2);
    }

    [Fact]
    public void Callers_Greet_FindsOldGreetCaller()
    {
        var result = GetCallGraphLogic.Execute(
            _loaded, _resolver, _metadata, "Greeter.Greet", "callers", 3, 500)!;

        Assert.Equal("callers", result.Direction);
        Assert.Empty(result.Callees);
        Assert.NotEmpty(result.Callers);

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
