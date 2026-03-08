using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

[McpServerToolType]
public static class SearchSymbolsTool
{
    [McpServerTool(Name = "search_symbols"),
     Description("Search for types, methods, properties, and fields by name (case-insensitive substring match, max 50 results)")]
    public static IReadOnlyList<SymbolLocation> Execute(
        SolutionManager manager,
        [Description("Search query (substring match against symbol names)")] string query)
    {
        manager.EnsureLoaded();
        return SearchSymbolsLogic.Execute(manager.GetResolver(), query);
    }
}
