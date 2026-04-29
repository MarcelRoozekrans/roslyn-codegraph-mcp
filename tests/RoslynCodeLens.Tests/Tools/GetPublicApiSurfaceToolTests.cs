using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GetPublicApiSurfaceToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(PublicApiKind.Class)]
    [InlineData(PublicApiKind.Struct)]
    [InlineData(PublicApiKind.Interface)]
    [InlineData(PublicApiKind.Enum)]
    [InlineData(PublicApiKind.Record)]
    [InlineData(PublicApiKind.RecordStruct)]
    [InlineData(PublicApiKind.Delegate)]
    [InlineData(PublicApiKind.Constructor)]
    [InlineData(PublicApiKind.Method)]
    [InlineData(PublicApiKind.Property)]
    [InlineData(PublicApiKind.Indexer)]
    [InlineData(PublicApiKind.Field)]
    [InlineData(PublicApiKind.Event)]
    [InlineData(PublicApiKind.Operator)]
    public void EachKind_HasAtLeastOneEntry(PublicApiKind kind)
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        Assert.Contains(result.Entries, e => e.Kind == kind);
    }

    [Fact]
    public void Result_ContainsKnownPublicTypes()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        Assert.Contains(result.Entries, e => e.Name == "TestLib.Greeter" && e.Kind == PublicApiKind.Class);
        Assert.Contains(result.Entries, e => e.Name == "TestLib.IGreeter" && e.Kind == PublicApiKind.Interface);
        Assert.Contains(result.Entries, e => e.Name == "TestLib.FancyGreeter" && e.Kind == PublicApiKind.Class);
        Assert.Contains(result.Entries, e => e.Name == "TestLib.OrderRecord" && e.Kind == PublicApiKind.Record);
        Assert.Contains(result.Entries, e => e.Name == "TestLib.PointStruct" && e.Kind == PublicApiKind.RecordStruct);
        Assert.Contains(result.Entries, e => e.Name == "TestLib.PriorityLevel" && e.Kind == PublicApiKind.Enum);
        Assert.Contains(result.Entries, e => e.Name == "TestLib.OrderProcessedHandler" && e.Kind == PublicApiKind.Delegate);
    }

    [Fact]
    public void Result_RecordPositionalProperty_IsIncluded()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        Assert.Contains(result.Entries, e =>
            e.Kind == PublicApiKind.Property &&
            e.Name.EndsWith(".OrderRecord.Id", StringComparison.Ordinal));
        Assert.Contains(result.Entries, e =>
            e.Kind == PublicApiKind.Property &&
            e.Name.EndsWith(".OrderRecord.Name", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_DoesNotIncludeInternalTypes()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.Entries, e => e.Name.EndsWith(".InternalHidden", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Entries, e => e.Name.EndsWith(".NotApi", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_DoesNotIncludeProtectedOnSealed()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.Entries, e =>
            e.Name.EndsWith(".SealedHolder.HiddenProtected", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_IncludesProtectedOnNonSealed()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        Assert.Contains(result.Entries, e =>
            e.Name.Contains(".AbstractProcessor.Process", StringComparison.Ordinal) &&
            e.Accessibility == PublicApiAccessibility.Protected);
        Assert.Contains(result.Entries, e =>
            e.Name.EndsWith(".AbstractProcessor.Counter", StringComparison.Ordinal) &&
            e.Accessibility == PublicApiAccessibility.Protected);
    }

    [Fact]
    public void Result_DoesNotIncludePublicTypeNestedInInternalContainer()
    {
        // OuterInternal is internal; its nested public class LeakedNested is unreachable
        // from outside the assembly. Neither it nor its members should appear.
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.Entries, e => e.Name.EndsWith(".LeakedNested", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Entries, e => e.Name.Contains("LeakedNested.LeakedMethod", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_OverloadsHaveDistinctEntries()
    {
        // OverloadSample.DoSomething(int) and DoSomething(string) must be distinct entries.
        // Without parameter types in the name, they would collide.
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        var overloads = result.Entries
            .Where(e => e.Kind == PublicApiKind.Method &&
                        e.Name.Contains("OverloadSample.DoSomething", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, overloads.Count);
        // Names must include parameter types (so they're distinct)
        Assert.Contains(overloads, e => e.Name.Contains("(int)", StringComparison.Ordinal));
        Assert.Contains(overloads, e => e.Name.Contains("(string)", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_DoesNotIncludeTestProjectMembers()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        var testProjectNames = RoslynCodeLens.TestDiscovery.TestProjectDetector
            .GetTestProjectIds(_loaded.Solution)
            .Select(id => _loaded.Solution.GetProject(id)!.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(result.Entries, e => testProjectNames.Contains(e.Project));
    }

    [Fact]
    public void Result_DoesNotIncludeImplicitMembers()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.Entries, e =>
            e.Name.EndsWith(".OrderRecord.EqualityContract", StringComparison.Ordinal));
    }

    [Fact]
    public void Entries_SortedByNameAscending()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        for (int i = 1; i < result.Entries.Count; i++)
        {
            Assert.True(
                string.CompareOrdinal(result.Entries[i - 1].Name, result.Entries[i].Name) <= 0,
                $"Sort violation at index {i}: '{result.Entries[i - 1].Name}' > '{result.Entries[i].Name}'");
        }
    }

    [Fact]
    public void Summary_TotalMatchesListLength()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        Assert.Equal(result.Entries.Count, result.Summary.TotalEntries);
    }

    [Fact]
    public void Summary_ByKindCountsAreCorrect()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        foreach (var (kindName, count) in result.Summary.ByKind)
        {
            var actual = result.Entries.Count(e => e.Kind.ToString() == kindName);
            Assert.Equal(actual, count);
        }
    }

    [Fact]
    public void Summary_ByProjectCountsAreCorrect()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        foreach (var (projectName, count) in result.Summary.ByProject)
        {
            var actual = result.Entries.Count(e => e.Project == projectName);
            Assert.Equal(actual, count);
        }
    }

    [Fact]
    public void Summary_ByAccessibilityCountsAreCorrect()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        foreach (var (accName, count) in result.Summary.ByAccessibility)
        {
            var actual = result.Entries.Count(e => e.Accessibility.ToString() == accName);
            Assert.Equal(actual, count);
        }
    }

    [Fact]
    public void Entries_HaveLocationInfo()
    {
        var result = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);

        foreach (var e in result.Entries)
        {
            Assert.NotEmpty(e.FilePath);
            Assert.True(e.Line > 0);
            Assert.NotEmpty(e.Project);
            Assert.NotEmpty(e.Name);
        }
    }
}
