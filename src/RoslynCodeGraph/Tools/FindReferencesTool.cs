using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

[McpServerToolType]
public static class FindReferencesTool
{
    [McpServerTool(Name = "find_references"),
     Description("Find all references to a symbol (type, method, property, field, or event) across the solution")]
    public static IReadOnlyList<SymbolReference> Execute(
        SolutionManager manager,
        [Description("Symbol name: simple type (MyClass), fully qualified (Namespace.MyClass), or member (MyClass.MyProperty)")] string symbol)
    {
        manager.EnsureLoaded();
        return FindReferencesLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), symbol);
    }
}
