using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class AnalyzeChangeImpactToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public AnalyzeChangeImpactToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void Execute_ForInterfaceMethod_ReturnsImpact()
    {
        var result = AnalyzeChangeImpactLogic.Execute(_loaded, _resolver, _metadata, "IGreeter.Greet");

        Assert.NotNull(result);
        Assert.True(result.DirectReferenceCount > 0 || result.CallerCount > 0);
        Assert.NotEmpty(result.AffectedFiles);
        Assert.NotEmpty(result.AffectedProjects);
    }

    [Fact]
    public void Execute_ForUnknownSymbol_ReturnsNull()
    {
        var result = AnalyzeChangeImpactLogic.Execute(_loaded, _resolver, _metadata, "NonExistentClass.NoMethod");

        Assert.Null(result);
    }
}
