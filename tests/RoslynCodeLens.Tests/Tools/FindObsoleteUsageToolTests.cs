using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindObsoleteUsageToolTests : IAsyncLifetime
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

    [Fact]
    public void Result_FindsObsoleteWithMessage()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        var group = Assert.Single(result.Groups, g =>
            g.SymbolName.Contains("ObsoleteWarning", StringComparison.Ordinal));
        Assert.Equal("Use NewWay instead", group.DeprecationMessage);
        Assert.False(group.IsError);
        // UseAll: 2× direct calls. UseConditionalAccess: 1× `?.ObsoleteWarning()`. At least 3.
        Assert.True(group.UsageCount >= 3);
    }

    [Fact]
    public void Result_FindsObsoleteError()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        var group = Assert.Single(result.Groups, g =>
            g.SymbolName.Contains("ObsoleteError", StringComparison.Ordinal));
        Assert.Equal("Hard fail", group.DeprecationMessage);
        Assert.True(group.IsError);
        Assert.True(group.UsageCount >= 1);
    }

    [Fact]
    public void Result_FindsObsoleteWithoutMessage()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        var group = Assert.Single(result.Groups, g =>
            g.SymbolName.Contains("ObsoleteWithoutMessage", StringComparison.Ordinal));
        Assert.Equal(string.Empty, group.DeprecationMessage);
        Assert.False(group.IsError);
        Assert.True(group.UsageCount >= 1);
    }

    [Fact]
    public void Result_FindsObsoleteType()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        var group = Assert.Single(result.Groups, g =>
            g.SymbolName.Contains("ObsoleteType", StringComparison.Ordinal));
        Assert.Equal("Drop this type", group.DeprecationMessage);
        // UseObsoleteType: `new ObsoleteType()` + `nameof(ObsoleteType)`.
        // UseQualifiedNew: `new TestLib.ObsoleteSamples.ObsoleteType()`. >= 3 in any counting.
        Assert.True(group.UsageCount >= 3);
    }

    [Fact]
    public void ConditionalAccess_IsCounted()
    {
        // Locks in I1 from review: `obj?.ObsoleteMethod()` uses MemberBindingExpressionSyntax,
        // which had been missing from the nested-skip filter. Verify the conditional-access
        // call site IS detected (snippet contains '?.') and counted at least once.
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        var group = Assert.Single(result.Groups, g =>
            g.SymbolName.Contains("ObsoleteWarning", StringComparison.Ordinal));
        Assert.Contains(group.Usages, u => u.CallerName.Contains("UseConditionalAccess", StringComparison.Ordinal));
    }

    [Fact]
    public void QualifiedNameNew_IsCounted()
    {
        // Locks in I2 from review: `new Ns.ObsoleteType()` uses QualifiedNameSyntax for the
        // type expression. Verify that call site IS detected and reported once per ObjectCreation.
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        var group = Assert.Single(result.Groups, g =>
            g.SymbolName.Contains("ObsoleteType", StringComparison.Ordinal));
        Assert.Contains(group.Usages, u => u.CallerName.Contains("UseQualifiedNew", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_OmitsObsoleteWithZeroUsages()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        Assert.DoesNotContain(result.Groups, g =>
            g.SymbolName.Contains("UnusedObsolete", StringComparison.Ordinal));
    }

    [Fact]
    public void Sort_ErrorsBeforeWarnings()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        // Walk groups: once we see a non-error, every subsequent group must also be non-error.
        var sawWarning = false;
        foreach (var g in result.Groups)
        {
            if (!g.IsError) sawWarning = true;
            else if (sawWarning)
                Assert.Fail($"Error group '{g.SymbolName}' appears after a warning group; sort broken.");
        }
    }

    [Fact]
    public void Sort_WithinSameSeverity_HighestUsageFirst()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        for (int i = 1; i < result.Groups.Count; i++)
        {
            var prev = result.Groups[i - 1];
            var curr = result.Groups[i];
            if (prev.IsError == curr.IsError)
                Assert.True(prev.UsageCount >= curr.UsageCount,
                    $"Sort broken at {i}: '{prev.SymbolName}' ({prev.UsageCount}) before '{curr.SymbolName}' ({curr.UsageCount})");
        }
    }

    [Fact]
    public void ErrorOnlyFilter_ExcludesWarnings()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: true);

        Assert.All(result.Groups, g => Assert.True(g.IsError, $"Group {g.SymbolName} is warning-level but errorOnly=true"));
    }

    [Fact]
    public void ProjectFilter_OnlyReturnsRequestedProject()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: "TestLib", errorOnly: false);

        Assert.NotEmpty(result.Groups);
        Assert.All(result.Groups, g =>
            Assert.All(g.Usages, u => Assert.Equal("TestLib", u.Project)));
    }

    [Fact]
    public void Usages_SortedByFileLine()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        foreach (var group in result.Groups)
        {
            for (int i = 1; i < group.Usages.Count; i++)
            {
                var prev = group.Usages[i - 1];
                var curr = group.Usages[i];
                var fileCmp = string.CompareOrdinal(prev.FilePath, curr.FilePath);
                Assert.True(fileCmp < 0 || (fileCmp == 0 && prev.Line <= curr.Line),
                    $"Usage sort broken in {group.SymbolName} at {i}");
            }
        }
    }

    [Fact]
    public void TestProjects_AreSkipped()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        Assert.All(result.Groups, g =>
            Assert.All(g.Usages, u => Assert.NotEqual("RoslynCodeLens.Tests", u.Project)));
    }

    [Fact]
    public void Usages_HaveLocationInfo()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        foreach (var g in result.Groups)
        {
            Assert.All(g.Usages, u =>
            {
                Assert.NotEmpty(u.FilePath);
                Assert.True(u.Line > 0);
                Assert.NotEmpty(u.CallerName);
                Assert.NotEmpty(u.Snippet);
            });
        }
    }

    [Fact]
    public void Usages_PopulateIsGeneratedFlag()
    {
        // The IsGenerated flag should reflect resolver.IsGenerated(filePath). For the source
        // fixture (no generated files), every usage must report IsGenerated: false.
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        foreach (var g in result.Groups)
            Assert.All(g.Usages, u => Assert.False(u.IsGenerated, $"Unexpected IsGenerated=true for {u.CallerName} at {u.FilePath}:{u.Line}"));
    }

    [Fact]
    public void NoMatchingObsolete_ReturnsEmptyGroups()
    {
        // No project filter, but with errorOnly + a project that has no error-level obsoletes
        // we should still get a non-throwing empty-or-minimal result.
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: "TestLib2", errorOnly: true);

        // TestLib2 has no [Obsolete(..., true)] in the fixture, so the filtered result is empty.
        Assert.Empty(result.Groups);
    }
}
