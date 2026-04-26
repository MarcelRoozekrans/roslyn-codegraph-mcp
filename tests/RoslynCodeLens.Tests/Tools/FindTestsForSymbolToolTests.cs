using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindTestsForSymbolToolTests : IAsyncLifetime
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
    public void Direct_FindsNUnitAndMSTestTestsForGreeter()
    {
        var result = FindTestsForSymbolLogic.Execute(
            _loaded, _resolver, "Greeter.Greet", transitive: false, maxDepth: 3);

        Assert.Contains(result.DirectTests, t => t.Framework == TestFramework.NUnit);
        Assert.Contains(result.DirectTests, t => t.Framework == TestFramework.MSTest);
    }

    [Fact]
    public void Direct_DoesNotIncludeTransitiveCallers()
    {
        var result = FindTestsForSymbolLogic.Execute(
            _loaded, _resolver, "Greeter.Greet", transitive: false, maxDepth: 3);

        // TransitiveGreetTest (NUnit) reaches Greet only via HelperThatGreets — it must
        // NOT appear in direct mode.
        Assert.DoesNotContain(result.DirectTests, t =>
            t.FullyQualifiedName.Contains("TransitiveGreetTest", StringComparison.Ordinal));

        Assert.Empty(result.TransitiveTests);
    }

    [Fact]
    public void Direct_IncludesEachAttributeOnceEvenForDataDriven()
    {
        var result = FindTestsForSymbolLogic.Execute(
            _loaded, _resolver, "Greeter.Greet", transitive: false, maxDepth: 3);

        // ParameterisedGreetTest exists in both NUnit and MSTest fixtures with multiple
        // data rows; it should appear once per framework, not per row.
        var nunitParameterised = result.DirectTests.Where(t =>
            t.Framework == TestFramework.NUnit &&
            t.FullyQualifiedName.EndsWith("ParameterisedGreetTest", StringComparison.Ordinal)).ToList();
        Assert.Single(nunitParameterised);
    }

    [Fact]
    public void Direct_UnknownSymbol_ReturnsEmpty()
    {
        var result = FindTestsForSymbolLogic.Execute(
            _loaded, _resolver, "DoesNotExist.Method", transitive: false, maxDepth: 3);

        Assert.Empty(result.DirectTests);
        Assert.Empty(result.TransitiveTests);
    }

    [Fact]
    public void Transitive_FindsTestViaHelper()
    {
        var result = FindTestsForSymbolLogic.Execute(
            _loaded, _resolver, "Greeter.Greet", transitive: true, maxDepth: 3);

        // TransitiveGreetTest reaches Greet only through HelperThatGreets.
        var transitive = result.TransitiveTests.SingleOrDefault(t =>
            t.FullyQualifiedName.EndsWith("TransitiveGreetTest", StringComparison.Ordinal));

        Assert.NotNull(transitive);
        Assert.NotNull(transitive!.CallChain);
        Assert.Contains("HelperThatGreets", transitive.CallChain!);
        Assert.Equal("Greet", transitive.CallChain![^1]);  // target last
    }

    [Fact]
    public void Transitive_DirectHitsStillInDirectBucket()
    {
        var result = FindTestsForSymbolLogic.Execute(
            _loaded, _resolver, "Greeter.Greet", transitive: true, maxDepth: 3);

        // DirectGreetTest is a direct caller; it must remain in DirectTests, not TransitiveTests.
        Assert.Contains(result.DirectTests, t =>
            t.FullyQualifiedName.EndsWith("DirectGreetTest", StringComparison.Ordinal));
        Assert.DoesNotContain(result.TransitiveTests, t =>
            t.FullyQualifiedName.EndsWith("DirectGreetTest", StringComparison.Ordinal));
    }

    [Fact]
    public void Transitive_RespectsMaxDepth()
    {
        // maxDepth=1 means the walk only inspects direct callers. The transitive helper
        // (depth-2 from the target) must not produce a transitive hit.
        var result = FindTestsForSymbolLogic.Execute(
            _loaded, _resolver, "Greeter.Greet", transitive: true, maxDepth: 1);

        Assert.DoesNotContain(result.TransitiveTests, t =>
            t.FullyQualifiedName.EndsWith("TransitiveGreetTest", StringComparison.Ordinal));
    }
}
