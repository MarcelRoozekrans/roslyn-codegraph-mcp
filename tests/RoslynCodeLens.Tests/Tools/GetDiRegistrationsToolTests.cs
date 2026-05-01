using RoslynCodeLens;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetDiRegistrationsToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GetDiRegistrationsToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void FindDiRegistrations_ForIGreeter_ReturnsRegistration()
    {
        var results = GetDiRegistrationsLogic.Execute(_loaded, _resolver, "IGreeter");

        Assert.Single(results);
        Assert.Equal("Scoped", results[0].Lifetime);
        Assert.Contains("Greeter", results[0].Implementation, StringComparison.Ordinal);
    }
}
