namespace RoslynCodeLens.Models;

public record EventSubscriberInfo(
    string EventName,
    string HandlerName,
    SubscriptionKind Kind,
    string FilePath,
    int Line,
    string Snippet,
    string Project,
    bool IsGenerated);
