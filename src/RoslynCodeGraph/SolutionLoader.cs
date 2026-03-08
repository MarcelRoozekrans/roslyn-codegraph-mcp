using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynCodeGraph;

public class SolutionLoader
{
    public async Task<LoadedSolution> LoadAsync(string solutionPath)
    {
        var workspace = MSBuildWorkspace.Create();

        workspace.WorkspaceFailed += (_, e) =>
        {
            Console.Error.WriteLine($"[roslyn-codegraph] Warning: {e.Diagnostic.Message}");
        };

        await Console.Error.WriteLineAsync($"[roslyn-codegraph] Loading solution: {Path.GetFileName(solutionPath)}").ConfigureAwait(false);
        var solution = await workspace.OpenSolutionAsync(solutionPath).ConfigureAwait(false);

        var compilations = new Dictionary<ProjectId, Compilation>();
        var projects = solution.Projects.ToList();

        for (var i = 0; i < projects.Count; i++)
        {
            var project = projects[i];
            await Console.Error.WriteLineAsync(
                $"[roslyn-codegraph] Compiling project {i + 1}/{projects.Count}: {project.Name}").ConfigureAwait(false);

            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            if (compilation != null)
            {
                compilations[project.Id] = compilation;
            }
        }

        await Console.Error.WriteLineAsync(
            $"[roslyn-codegraph] Ready. {compilations.Count} projects compiled.").ConfigureAwait(false);

        return new LoadedSolution
        {
            Solution = solution,
            Compilations = compilations
        };
    }

    public static string? FindSolutionFile(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir != null)
        {
            var slnFiles = dir.GetFiles("*.sln")
                .Concat(dir.GetFiles("*.slnx"))
                .ToArray();
            if (slnFiles.Length > 0)
            {
                return slnFiles
                    .OrderBy(f => f.FullName.Length)
                    .First()
                    .FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
