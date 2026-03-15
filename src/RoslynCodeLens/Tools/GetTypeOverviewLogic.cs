using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class GetTypeOverviewLogic
{
    public static TypeOverview? Execute(LoadedSolution loaded, SymbolResolver resolver, string typeName)
    {
        var context = GetSymbolContextLogic.Execute(loaded, resolver, typeName);
        if (context == null)
            return null;

        var hierarchy = GetTypeHierarchyLogic.Execute(loaded, resolver, typeName);

        // Diagnostics scoped to the file containing the type
        var diagnostics = GetDiagnosticsLogic.Execute(loaded, resolver, project: null, severity: null)
            .Where(d => !string.IsNullOrEmpty(context.File) &&
                        d.File.Equals(context.File, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new TypeOverview(context, hierarchy, diagnostics);
    }
}
