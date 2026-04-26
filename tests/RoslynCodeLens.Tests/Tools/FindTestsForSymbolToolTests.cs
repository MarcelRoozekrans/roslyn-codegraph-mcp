using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindTestsForSymbolToolTests : IAsyncLifetime
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
    public void Direct_FindsNUnitAndMSTestTestsForGreeter()
    {
        var result = FindTestsForSymbolLogic.Execute(
            _loaded, _resolver, _metadata, "Greeter.Greet", transitive: false, maxDepth: 3);

        Assert.Contains(result.DirectTests, t => t.Framework == TestFramework.NUnit);
        Assert.Contains(result.DirectTests, t => t.Framework == TestFramework.MSTest);
    }

    [Fact]
    public void Direct_DoesNotIncludeTransitiveCallers()
    {
        var result = FindTestsForSymbolLogic.Execute(
            _loaded, _resolver, _metadata, "Greeter.Greet", transitive: false, maxDepth: 3);

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
            _loaded, _resolver, _metadata, "Greeter.Greet", transitive: false, maxDepth: 3);

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
            _loaded, _resolver, _metadata, "DoesNotExist.Method", transitive: false, maxDepth: 3);

        Assert.Empty(result.DirectTests);
        Assert.Empty(result.TransitiveTests);
    }
}
