using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindGodObjectsToolTests : IAsyncLifetime
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

    [Fact]
    public void Result_FindsKnownGodObject()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 1, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 1);

        Assert.Contains(result.Types, t =>
            t.TypeName.Contains("BadGodObject", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_DoesNotFlag_LargeButIsolated()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 1, minMembers: 15, minFields: 0,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 1);

        Assert.DoesNotContain(result.Types, t =>
            t.TypeName.Contains("LargeButIsolated", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_DoesNotFlag_SmallButHighlyCoupled()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 1, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 1);

        Assert.DoesNotContain(result.Types, t =>
            t.TypeName.Contains("SmallButHighlyCoupled", StringComparison.Ordinal));
    }

    [Fact]
    public void IncomingNamespaces_AreCounted()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 1, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 1);

        var bad = Assert.Single(result.Types, t =>
            t.TypeName.Contains("BadGodObject", StringComparison.Ordinal));
        Assert.True(bad.IncomingNamespaces >= 5);
    }

    [Fact]
    public void IncomingNamespaces_ExcludesOwnNamespace()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 1, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 1);

        var bad = Assert.Single(result.Types, t =>
            t.TypeName.Contains("BadGodObject", StringComparison.Ordinal));
        Assert.DoesNotContain(bad.SampleIncoming, ns =>
            ns == "TestLib.GodObjectSamples.Bad");
    }

    [Fact]
    public void OutgoingNamespaces_ExcludesBclTypes()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 1, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 0);

        var bad = Assert.Single(result.Types, t =>
            t.TypeName.Contains("BadGodObject", StringComparison.Ordinal));
        Assert.DoesNotContain(bad.SampleOutgoing, ns =>
            ns.StartsWith("System", StringComparison.Ordinal) ||
            ns.StartsWith("Microsoft", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_SortedByAxesExceededDesc()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: null,
            minLines: 1, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 0);

        for (int i = 1; i < result.Types.Count; i++)
        {
            var prev = result.Types[i - 1];
            var curr = result.Types[i];
            var prevTotal = prev.SizeAxesExceeded + prev.CouplingAxesExceeded;
            var currTotal = curr.SizeAxesExceeded + curr.CouplingAxesExceeded;
            Assert.True(prevTotal >= currTotal,
                $"Sort violation at {i}: prev={prevTotal}, curr={currTotal}");
        }
    }

    [Fact]
    public void ProjectFilter_OnlyReturnsRequestedProject()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 1, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 0);

        Assert.All(result.Types, t => Assert.Equal("TestLib", t.Project));
    }

    [Fact]
    public void Thresholds_AreConfigurable_HighThreshold_FiltersAll()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 100000, minMembers: 100000, minFields: 100000,
            minIncomingNamespaces: 100000, minOutgoingNamespaces: 100000);

        Assert.Empty(result.Types);
    }

    [Fact]
    public void SampleIncoming_LimitedToFive()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: "TestLib",
            minLines: 1, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 0);

        Assert.All(result.Types, t => Assert.True(t.SampleIncoming.Count <= 5));
        Assert.All(result.Types, t => Assert.True(t.SampleOutgoing.Count <= 5));
    }

    [Fact]
    public void Interfaces_AreNotFlagged()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: null,
            minLines: 1, minMembers: 1, minFields: 0,
            minIncomingNamespaces: 1, minOutgoingNamespaces: 0);

        Assert.DoesNotContain(result.Types, t =>
            t.TypeName.Contains("IGreeter", StringComparison.Ordinal));
    }

    [Fact]
    public void TestProjects_AreSkipped()
    {
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: null,
            minLines: 1, minMembers: 1, minFields: 0,
            minIncomingNamespaces: 1, minOutgoingNamespaces: 0);

        Assert.DoesNotContain(result.Types, t => t.Project == "RoslynCodeLens.Tests");
    }
}
