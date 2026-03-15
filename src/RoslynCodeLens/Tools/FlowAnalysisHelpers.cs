using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Tools;

internal static class FlowAnalysisHelpers
{
    internal static (Document? Document, Compilation? Compilation) FindDocument(LoadedSolution loaded, string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);

        foreach (var project in loaded.Solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath != null &&
                    doc.FilePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    loaded.Compilations.TryGetValue(project.Id, out var compilation);
                    return (doc, compilation);
                }
            }
        }

        return (null, null);
    }
}
