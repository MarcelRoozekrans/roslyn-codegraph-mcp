using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

[McpServerToolType]
public static class GetSymbolContextTool
{
    [McpServerTool(Name = "get_symbol_context"),
     Description("One-shot context dump for a type: namespace, base class, interfaces, injected dependencies, public members")]
    public static SymbolContext? Execute(
        SolutionManager manager,
        [Description("Type name (simple or fully qualified)")] string symbol)
    {
        manager.EnsureLoaded();
        return GetSymbolContextLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), symbol);
    }
}
