using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Symbols;

[Collection("TestSolution")]
public class MetadataSymbolResolverTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _sourceResolver;
    private readonly MetadataSymbolResolver _metadata;

    public MetadataSymbolResolverTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _sourceResolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

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
