using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindEventSubscribersTool
{
    [McpServerTool(Name = "find_event_subscribers")]
    [Description(
        "Find every += and -= site for an event symbol across the solution. " +
        "Accepts source events (e.g. 'MyClass.Clicked') or metadata events " +
        "(e.g. 'System.Diagnostics.Process.Exited'). " +
        "Each result reports the source location, the resolved handler (method FQN, " +
        "or '<lambda at File.cs:N>' for inline handlers), and the subscription kind " +
        "(Subscribe for +=, Unsubscribe for -=). " +
        "Use this for memory-leak audits (compare subscribe/unsubscribe pairs), " +
        "UI event subscriber inspection, or when Grep over '+= EventName' would miss " +
        "qualified or fully-typed subscription sites. " +
        "Sort: file path ASC then line ASC.")]
    public static IReadOnlyList<EventSubscriberInfo> Execute(
        MultiSolutionManager manager,
        [Description("Event symbol (e.g. 'MyClass.Clicked' or 'System.Diagnostics.Process.Exited')")]
        string symbol)
    {
        manager.EnsureLoaded();
        return FindEventSubscribersLogic.Execute(
            manager.GetLoadedSolution(),
            manager.GetResolver(),
            manager.GetMetadataResolver(),
            symbol);
    }
}
