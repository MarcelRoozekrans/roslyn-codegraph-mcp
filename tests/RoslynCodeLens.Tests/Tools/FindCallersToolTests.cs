using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindCallersToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public FindCallersToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void FindCallers_ForMethod_ReturnsCallSites()
    {
        var results = FindCallersLogic.Execute(_loaded, _resolver, _metadata, "IGreeter.Greet");

        Assert.Contains(results, r => r.Caller.Contains("GreeterConsumer", StringComparison.Ordinal));
    }

    [Fact]
    public void FindCallers_MetadataExtensionMethod_FindsSourceInvocations()
    {
        var results = FindCallersLogic.Execute(
            _loaded, _resolver, _metadata,
            "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped");

        Assert.NotEmpty(results);
    }
}
