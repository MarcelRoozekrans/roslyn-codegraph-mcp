using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindUncoveredSymbolsToolTests : IAsyncLifetime
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
    public void Result_GreetFormal_AppearsAsUncovered()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        Assert.Contains(result.UncoveredSymbols, s =>
            s.Symbol.EndsWith("GreetFormal", StringComparison.Ordinal) &&
            s.Kind == UncoveredSymbolKind.Method);
    }

    [Fact]
    public void Result_Greet_DoesNotAppearAsUncovered()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.UncoveredSymbols, s =>
            string.Equals(s.Symbol, "Greeter.Greet", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_FormalNameLength_AppearsAsProperty()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        var symbol = Assert.Single(result.UncoveredSymbols, s =>
            s.Symbol.EndsWith("FormalNameLength", StringComparison.Ordinal));
        Assert.Equal(UncoveredSymbolKind.Property, symbol.Kind);
        Assert.Equal(2, symbol.Complexity);
    }

    [Fact]
    public void Result_ClassifyName_HasHighComplexity()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        var symbol = Assert.Single(result.UncoveredSymbols, s =>
            s.Symbol.EndsWith("ClassifyName", StringComparison.Ordinal));
        Assert.True(symbol.Complexity >= 5,
            $"ClassifyName should have complexity >= 5; was {symbol.Complexity}");
    }

    [Fact]
    public void Result_Summary_CountsAddUp()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        var s = result.Summary;
        Assert.Equal(s.TotalSymbols, s.CoveredCount + s.UncoveredCount);
        Assert.Equal(s.UncoveredCount, result.UncoveredSymbols.Count);
        Assert.InRange(s.CoveragePercent, 0, 100);
    }

    [Fact]
    public void Result_Summary_RiskHotspotCount_CountsHighComplexityUncovered()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        var actualHotspots = result.UncoveredSymbols.Count(s => s.Complexity >= 5);
        Assert.Equal(actualHotspots, result.Summary.RiskHotspotCount);
        Assert.True(result.Summary.RiskHotspotCount >= 1,
            "ClassifyName alone should make this >= 1");
    }

    [Fact]
    public void Result_UncoveredSymbols_SortedByComplexityDescending()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        for (int i = 1; i < result.UncoveredSymbols.Count; i++)
        {
            Assert.True(
                result.UncoveredSymbols[i - 1].Complexity >= result.UncoveredSymbols[i].Complexity,
                $"Sort violation at index {i}: {result.UncoveredSymbols[i - 1].Complexity} < {result.UncoveredSymbols[i].Complexity}");
        }
    }

    [Fact]
    public void Result_UncoveredSymbols_HaveLocationInfo()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        foreach (var s in result.UncoveredSymbols)
        {
            Assert.NotEmpty(s.FilePath);
            Assert.True(s.Line > 0);
            Assert.NotEmpty(s.Project);
        }
    }

    [Fact]
    public void Result_DoesNotIncludeTestProjectMembers()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        var testProjectNames = TestProjectDetector
            .GetTestProjectIds(_loaded.Solution)
            .Select(id => _loaded.Solution.GetProject(id)!.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(result.UncoveredSymbols, s =>
            testProjectNames.Contains(s.Project));
    }

    [Fact]
    public void Result_UnreachedOverride_AppearsAsUncovered()
    {
        // Strict reference-based coverage: FancyGreeter.Greet overrides the (covered)
        // Greeter.Greet, but no test instantiates FancyGreeter or calls its override.
        // It must appear as uncovered — propagating coverage through the override chain
        // would silently hide real testing debt.
        var result = FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);

        Assert.Contains(result.UncoveredSymbols, s =>
            string.Equals(s.Symbol, "FancyGreeter.Greet", StringComparison.Ordinal));
    }
}
