using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetDiagnosticsLogic
{
    public static List<DiagnosticInfo> Execute(LoadedSolution loaded, SymbolResolver resolver, string? project, string? severity)
    {
        var minSeverity = severity?.ToLowerInvariant() switch
        {
            "error" => DiagnosticSeverity.Error,
            "warning" => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Warning // default: show warnings and errors
        };

        var results = new List<DiagnosticInfo>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var projectName = resolver.GetProjectName(projectId);

            if (project != null &&
                !projectName.Contains(project, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var diagnostic in compilation.GetDiagnostics())
            {
                if (diagnostic.Severity < minSeverity)
                    continue;

                var lineSpan = diagnostic.Location.GetLineSpan();
                var file = lineSpan.Path ?? "";
                var line = lineSpan.StartLinePosition.Line + 1;

                results.Add(new DiagnosticInfo(
                    diagnostic.Id,
                    diagnostic.Severity.ToString(),
                    diagnostic.GetMessage(),
                    file,
                    line,
                    projectName));
            }
        }

        return results;
    }
}

[McpServerToolType]
public static class GetDiagnosticsTool
{
    [McpServerTool(Name = "get_diagnostics"),
     Description("List compiler errors and warnings across the solution")]
    public static List<DiagnosticInfo> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Optional project name filter")] string? project = null,
        [Description("Minimum severity: 'error' or 'warning' (default: warning)")] string? severity = null)
    {
        SolutionGuard.EnsureLoaded(loaded);
        return GetDiagnosticsLogic.Execute(loaded, resolver, project, severity);
    }
}
