using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class FindEventSubscribersToolTests : IAsyncLifetime
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
    public void UnknownSymbol_ReturnsEmpty()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "Does.Not.Exist");

        Assert.Empty(results);
    }

    [Fact]
    public void Subscribe_MethodGroup_ReportsHandlerFqn()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        var match = Assert.Single(results, r =>
            r.Kind == SubscriptionKind.Subscribe &&
            r.HandlerName.Contains("OnClicked", StringComparison.Ordinal) &&
            r.FilePath.EndsWith("EventSubscriberSamples.cs", StringComparison.Ordinal) &&
            !r.Snippet.Contains("Clicked2", StringComparison.Ordinal));

        Assert.True(match.Line > 0);
        Assert.Equal("TestLib", match.Project);
    }

    [Fact]
    public void Subscribe_Lambda_ReportsSyntheticName()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        Assert.Contains(results, r =>
            r.Kind == SubscriptionKind.Subscribe &&
            r.HandlerName.StartsWith("<lambda at ", StringComparison.Ordinal) &&
            r.HandlerName.Contains("EventSubscriberSamples.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Subscribe_AnonymousMethod_ReportsSyntheticName()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        Assert.Contains(results, r =>
            r.Kind == SubscriptionKind.Subscribe &&
            r.HandlerName.StartsWith("<anonymous-method at ", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsubscribe_TaggedAsUnsubscribe()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        Assert.Contains(results, r =>
            r.Kind == SubscriptionKind.Unsubscribe &&
            r.HandlerName.Contains("OnClicked", StringComparison.Ordinal));
    }

    [Fact]
    public void InterfaceEvent_MatchesImplementations()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "IBusEventPublisher.MessageReceived");

        Assert.True(results.Count >= 2,
            $"Expected >=2 subscribers across implementations, got {results.Count}");
    }

    [Fact]
    public void TwoSubscriptionsOnSameLine_BothReported()
    {
        var clickedResults = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");
        var clicked2Results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked2");

        Assert.Contains(clickedResults, r =>
            r.Snippet.Contains("Clicked", StringComparison.Ordinal) &&
            !r.Snippet.Contains("Clicked2", StringComparison.Ordinal));
        Assert.Contains(clicked2Results, r =>
            r.Snippet.Contains("Clicked2", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_SortedByFileLine()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        for (int i = 1; i < results.Count; i++)
        {
            var prev = results[i - 1];
            var curr = results[i];
            var fileCmp = string.CompareOrdinal(prev.FilePath, curr.FilePath);
            Assert.True(
                fileCmp < 0 || (fileCmp == 0 && prev.Line <= curr.Line),
                $"Sort violation at {i}: '{prev.FilePath}:{prev.Line}' before '{curr.FilePath}:{curr.Line}'");
        }
    }

    [Fact]
    public void EventName_IsFullyQualified()
    {
        var results = FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");

        Assert.NotEmpty(results);
        foreach (var r in results)
        {
            Assert.Contains("Clicked", r.EventName, StringComparison.Ordinal);
            Assert.NotEmpty(r.Project);
        }
    }
}
