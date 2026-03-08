using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

[McpServerToolType]
public static class FindReflectionUsageTool
{
    [McpServerTool(Name = "find_reflection_usage"),
     Description("Detect dynamic/reflection-based usage like Type.GetType, Activator.CreateInstance, MethodInfo.Invoke")]
    public static IReadOnlyList<ReflectionUsage> Execute(
        SolutionManager manager,
        [Description("Optional type name to filter results (omit to scan entire solution)")] string? symbol = null)
    {
        manager.EnsureLoaded();
        return FindReflectionUsageLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), symbol);
    }
}
