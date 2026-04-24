using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindAttributeUsagesToolTests : IAsyncLifetime
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
    public void FindAttributeUsages_Obsolete_FindsMarkedMember()
    {
        var results = FindAttributeUsagesLogic.Execute(_loaded, _resolver, _metadata, "Obsolete");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.TargetName.Contains("OldGreet", StringComparison.Ordinal) && string.Equals(r.TargetKind, "method", StringComparison.Ordinal));
    }

    [Fact]
    public void FindAttributeUsages_Serializable_FindsMarkedType()
    {
        var results = FindAttributeUsagesLogic.Execute(_loaded, _resolver, _metadata, "Serializable");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.TargetName.Contains("Greeter", StringComparison.Ordinal) && string.Equals(r.TargetKind, "class", StringComparison.Ordinal));
    }

    [Fact]
    public void FindAttributeUsages_WithSuffix_StillMatches()
    {
        var results = FindAttributeUsagesLogic.Execute(_loaded, _resolver, _metadata, "ObsoleteAttribute");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.TargetName.Contains("OldGreet", StringComparison.Ordinal));
    }

    [Fact]
    public void FindAttributeUsages_NoMatch_ReturnsEmpty()
    {
        var results = FindAttributeUsagesLogic.Execute(_loaded, _resolver, _metadata, "NonExistentAttribute");

        Assert.Empty(results);
    }

    [Fact]
    public void FindAttributeUsages_FullyQualifiedMetadata_FindsUsage()
    {
        // When the caller passes the fully qualified name of a metadata attribute
        // (which is not in the simple-name index), the metadata resolver should
        // locate the attribute type and a full scan should still surface usages.
        var results = FindAttributeUsagesLogic.Execute(
            _loaded, _resolver, _metadata, "System.ObsoleteAttribute");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.TargetName.Contains("OldGreet", StringComparison.Ordinal));
    }
}
