using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

[McpServerToolType]
public static class GetSourceGeneratorsTool
{
    [McpServerTool(Name = "get_source_generators"),
     Description("List source generators and their output per project")]
    public static IReadOnlyList<SourceGeneratorInfo> Execute(
        SolutionManager manager,
        [Description("Optional project name filter")] string? project = null)
    {
        manager.EnsureLoaded();
        return GetSourceGeneratorsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), project);
    }
}
