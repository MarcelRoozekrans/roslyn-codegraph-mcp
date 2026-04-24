using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GetTypeHierarchyToolTests : IAsyncLifetime
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
    public void GetHierarchy_ForGreeter_ShowsBaseAndDerived()
    {
        var result = GetTypeHierarchyLogic.Execute(_resolver, _metadata, "Greeter");

        Assert.NotNull(result);
        Assert.Contains(result.Interfaces, i => i.FullName.Contains("IGreeter", StringComparison.Ordinal));
        Assert.Contains(result.Derived, d => d.FullName.Contains("FancyGreeter", StringComparison.Ordinal));
        Assert.Equal("source", result.Origin?.Kind);
    }

    [Fact]
    public void GetHierarchy_ForGreeter_HasNoBases()
    {
        var result = GetTypeHierarchyLogic.Execute(_resolver, _metadata, "Greeter");

        Assert.NotNull(result);
        Assert.Empty(result.Bases);
    }

    [Fact]
    public void GetHierarchy_ForFancyGreeter_ShowsGreeterAsBase()
    {
        var result = GetTypeHierarchyLogic.Execute(_resolver, _metadata, "FancyGreeter");

        Assert.NotNull(result);
        Assert.Contains(result.Bases, b => b.FullName.Contains("Greeter", StringComparison.Ordinal));
        Assert.Empty(result.Derived);
    }

    [Fact]
    public void GetHierarchy_ForUnknownType_ReturnsNull()
    {
        var result = GetTypeHierarchyLogic.Execute(_resolver, _metadata, "NonExistentType");

        Assert.Null(result);
    }

    [Fact]
    public void TypeHierarchy_MetadataInterface_HasOrigin()
    {
        var result = GetTypeHierarchyLogic.Execute(_resolver, _metadata,
            "Microsoft.Extensions.DependencyInjection.IServiceCollection");

        Assert.NotNull(result);
        Assert.Equal("metadata", result!.Origin?.Kind);
        // IServiceCollection : IList<ServiceDescriptor>, ICollection<ServiceDescriptor>, ...
        // -- the base-interface chain is present.
        Assert.NotEmpty(result.Interfaces);
    }
}
