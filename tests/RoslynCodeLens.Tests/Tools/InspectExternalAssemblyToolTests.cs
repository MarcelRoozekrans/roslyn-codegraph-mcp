using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class InspectExternalAssemblyToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private MetadataSymbolResolver _metadata = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _metadata = new MetadataSymbolResolver(_loaded, new SymbolResolver(_loaded));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Summary_ListsNamespacesAndCounts()
    {
        var result = InspectExternalAssemblyLogic.Execute(
            _metadata, "Microsoft.Extensions.DependencyInjection.Abstractions",
            mode: "summary", namespaceFilter: null);

        Assert.NotNull(result);
        Assert.Equal("summary", result!.Mode);
        Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions", result.Name);
        Assert.NotEmpty(result.NamespaceTree);
        Assert.Contains(result.NamespaceTree,
            n => string.Equals(n.Namespace, "Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal)
              && n.PublicTypeNames.Contains("IServiceCollection", StringComparer.Ordinal));
        Assert.Empty(result.Types);
    }

    [Fact]
    public void Namespace_ReturnsTypesWithMembers()
    {
        var result = InspectExternalAssemblyLogic.Execute(
            _metadata, "Microsoft.Extensions.DependencyInjection.Abstractions",
            mode: "namespace", namespaceFilter: "Microsoft.Extensions.DependencyInjection");

        Assert.NotNull(result);
        Assert.Equal("namespace", result!.Mode);
        Assert.Contains(result.Types, t => t.FullName.EndsWith("IServiceCollection", StringComparison.Ordinal));
        var svc = result.Types.First(t => t.FullName.EndsWith("IServiceCollection", StringComparison.Ordinal));
        Assert.Equal("interface", svc.Kind);
        Assert.NotEmpty(svc.Members);
    }

    [Fact]
    public void UnreferencedAssembly_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => InspectExternalAssemblyLogic.Execute(
            _metadata, "Some.NotReferenced.Library", mode: "summary", namespaceFilter: null));
        Assert.Contains("get_nuget_dependencies", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamespaceMode_UnknownNamespace_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => InspectExternalAssemblyLogic.Execute(
            _metadata, "Microsoft.Extensions.DependencyInjection.Abstractions",
            mode: "namespace", namespaceFilter: "NoSuch.Namespace"));
        Assert.Contains("NoSuch.Namespace", ex.Message, StringComparison.Ordinal);
    }
}
