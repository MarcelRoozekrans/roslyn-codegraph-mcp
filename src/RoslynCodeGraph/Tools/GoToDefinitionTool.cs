using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

[McpServerToolType]
public static class GoToDefinitionTool
{
    [McpServerTool(Name = "go_to_definition"),
     Description("Find the source file and line where a symbol is defined")]
    public static IReadOnlyList<SymbolLocation> Execute(
        SolutionManager manager,
        [Description("Symbol name: simple type (MyClass), fully qualified (Namespace.MyClass), or member (MyClass.DoWork)")] string symbol)
    {
        manager.EnsureLoaded();
        return GoToDefinitionLogic.Execute(manager.GetResolver(), symbol);
    }
}
