using RoslynCodeLens;
using RoslynCodeLens.Metadata;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class PeekIlToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;
    private MetadataSymbolResolver _metadata = null!;
    private IlDisassemblerAdapter _adapter = null!;
    private PEFileCache _peCache = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _resolver = new SymbolResolver(_loaded);
        _metadata = new MetadataSymbolResolver(_loaded, _resolver);
        _peCache = new PEFileCache();
        _adapter = new IlDisassemblerAdapter(_peCache);
    }

    public Task DisposeAsync() { _peCache.Dispose(); return Task.CompletedTask; }

    [Fact]
    public void Peek_MetadataCtor_ReturnsIlText()
    {
        var result = PeekIlLogic.Execute(
            _loaded, _metadata, _adapter,
            "Microsoft.Extensions.DependencyInjection.ServiceDescriptor..ctor(System.Type, System.Type, Microsoft.Extensions.DependencyInjection.ServiceLifetime)");

        Assert.NotNull(result);
        Assert.Contains("IL_", result!.Il, StringComparison.Ordinal);
        Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions", result.AssemblyName);
    }

    [Fact]
    public void Peek_SourceSymbol_Throws()
    {
        Assert.Throws<ArgumentException>(() => PeekIlLogic.Execute(
            _loaded, _metadata, _adapter, "TestLib.Greeter.Greet"));
    }

    [Fact]
    public void Peek_AbstractMethod_Throws()
    {
        Assert.Throws<ArgumentException>(() => PeekIlLogic.Execute(
            _loaded, _metadata, _adapter,
            "Microsoft.Extensions.DependencyInjection.IServiceCollection.Add"));
    }
}
