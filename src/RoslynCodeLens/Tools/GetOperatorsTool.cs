using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetOperatorsTool
{
    [McpServerTool(Name = "get_operators")]
    [Description("List user-defined and conversion operators on a type. Returns each operator's kind, signature, parameters, return type, and source location. Includes compiler-synthesized record equality operators.")]
    public static GetOperatorsResult Execute(
        MultiSolutionManager manager,
        [Description("Type name (simple or fully qualified) — e.g. Vector2, MyApp.Money")] string symbol)
    {
        manager.EnsureLoaded();
        return GetOperatorsLogic.Execute(manager.GetResolver(), manager.GetMetadataResolver(), symbol);
    }
}
