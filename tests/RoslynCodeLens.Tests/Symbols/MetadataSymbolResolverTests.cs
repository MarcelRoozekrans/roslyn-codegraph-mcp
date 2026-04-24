using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Tests.Symbols;

public class MetadataSymbolResolverTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _sourceResolver = null!;
    private MetadataSymbolResolver _metadata = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _sourceResolver = new SymbolResolver(_loaded);
        _metadata = new MetadataSymbolResolver(_loaded, _sourceResolver);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Resolve_MetadataType_ReturnsMetadataSymbol()
    {
        var result = _metadata.Resolve(
            "Microsoft.Extensions.DependencyInjection.IServiceCollection");

        Assert.NotNull(result);
        Assert.Equal("metadata", result.Origin.Kind);
        Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions", result.Origin.AssemblyName);
        Assert.False(string.IsNullOrEmpty(result.Origin.AssemblyVersion));
        Assert.True(result.Symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface });
    }

    [Fact]
    public void Resolve_SourceTypeTakesPrecedence()
    {
        var result = _metadata.Resolve("IGreeter");

        Assert.NotNull(result);
        Assert.Equal("source", result.Origin.Kind);
    }

    [Fact]
    public void Resolve_UnknownSymbol_ReturnsNull()
    {
        Assert.Null(_metadata.Resolve("Nothing.Here.AtAll"));
    }

    [Fact]
    public void Resolve_MetadataMember_ReturnsMethodSymbol()
    {
        var result = _metadata.Resolve(
            "Microsoft.Extensions.DependencyInjection.IServiceCollection.Add");

        Assert.NotNull(result);
        Assert.Equal("metadata", result.Origin.Kind);
        Assert.IsAssignableFrom<IMethodSymbol>(result.Symbol);
    }
}
