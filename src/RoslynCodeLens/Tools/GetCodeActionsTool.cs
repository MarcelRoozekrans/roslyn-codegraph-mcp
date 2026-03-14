using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetCodeActionsTool
{
    [McpServerTool(Name = "get_code_actions"),
     Description("List available code actions (refactorings and fixes) at a position in a C# file. " +
                 "Optionally specify endLine/endColumn to select a range for extract-method style refactorings. " +
                 "Returns action titles that can be passed to apply_code_action.")]
    public static async Task<IReadOnlyList<CodeActionInfo>> Execute(
        MultiSolutionManager manager,
        [Description("Full path to the C# source file")] string filePath,
        [Description("Line number (1-based)")] int line,
        [Description("Column number (1-based)")] int column,
        [Description("End line for text selection (1-based, optional)")] int? endLine = null,
        [Description("End column for text selection (1-based, optional)")] int? endColumn = null,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        return await GetCodeActionsLogic.ExecuteAsync(
            manager.GetLoadedSolution(), filePath, line, column,
            endLine, endColumn, ct).ConfigureAwait(false);
    }
}
