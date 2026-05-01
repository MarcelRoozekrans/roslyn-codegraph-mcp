using RoslynCodeLens;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tests.Fixtures;

/// <summary>
/// Shared fixture that loads the test solution once per assembly. Used by tests via
/// [Collection("TestSolution")]. Loading once eliminates per-test MSBuildWorkspace
/// flakiness on Linux CI and dramatically speeds up the test suite.
/// </summary>
public class TestSolutionFixture : IAsyncLifetime
{
    public string SolutionPath { get; private set; } = null!;
    public LoadedSolution Loaded { get; private set; } = null!;
    public SymbolResolver Resolver { get; private set; } = null!;
    public MetadataSymbolResolver Metadata { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        SolutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        Loaded = await new SolutionLoader().LoadAsync(SolutionPath).ConfigureAwait(false);
        Resolver = new SymbolResolver(Loaded);
        Metadata = new MetadataSymbolResolver(Loaded, Resolver);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// xUnit collection definition that binds TestSolutionFixture to the "TestSolution"
/// collection name. Tests opt in via [Collection("TestSolution")].
/// </summary>
[CollectionDefinition("TestSolution")]
public class TestSolutionCollection : ICollectionFixture<TestSolutionFixture> { }
