using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynCodeLens;

public class SolutionLoader
{
    public async Task<(Solution Solution, MSBuildWorkspace Workspace)> OpenAsync(string solutionPath)
    {
        var workspace = MSBuildWorkspace.Create();

        workspace.WorkspaceFailed += (_, e) =>
        {
            Console.Error.WriteLine($"[roslyn-codelens] Warning: {e.Diagnostic.Message}");
        };

        await Console.Error.WriteLineAsync($"[roslyn-codelens] Loading solution: {Path.GetFileName(solutionPath)}").ConfigureAwait(false);
        var solution = await workspace.OpenSolutionAsync(solutionPath).ConfigureAwait(false);

        return (solution, workspace);
    }

    public async Task<ConcurrentDictionary<ProjectId, Compilation>> CompileAllParallelAsync(Solution solution)
    {
        var compilations = new ConcurrentDictionary<ProjectId, Compilation>();
        var levels = GetCompilationLevels(solution);
        var totalProjects = levels.Sum(l => l.Count);
        var compiled = 0;

#pragma warning disable HLQ012, ZA0601 // async method — cannot use CollectionsMarshal.AsSpan across await; Select in loop is required for parallel task projection
        foreach (var level in levels)
        {
            var tasks = level.Select(async project =>
            {
                var index = Interlocked.Increment(ref compiled);
                await Console.Error.WriteLineAsync(
                    $"[roslyn-codelens] Compiling project {index}/{totalProjects}: {project.Name}").ConfigureAwait(false);

                var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
                if (compilation != null)
                {
                    compilations[project.Id] = compilation;
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
#pragma warning restore HLQ012, ZA0601

        return compilations;
    }

    public async Task<LoadedSolution> LoadAsync(string solutionPath)
    {
        var (solution, _) = await OpenAsync(solutionPath).ConfigureAwait(false);
        var compilations = await CompileAllParallelAsync(solution).ConfigureAwait(false);

        await Console.Error.WriteLineAsync(
            $"[roslyn-codelens] Ready. {compilations.Count} projects compiled.").ConfigureAwait(false);

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
            FileInfo? shortest = null;
            foreach (var f in dir.GetFiles("*.sln"))
            {
                if (shortest == null || f.FullName.Length < shortest.FullName.Length)
                    shortest = f;
            }
            foreach (var f in dir.GetFiles("*.slnx"))
            {
                if (shortest == null || f.FullName.Length < shortest.FullName.Length)
                    shortest = f;
            }
            if (shortest != null)
                return shortest.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Returns projects grouped by dependency level (leaves first).
    /// Level 0 = no project dependencies, Level 1 = depends only on level 0, etc.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Project>> GetCompilationLevels(Solution solution)
    {
        var projects = solution.Projects.ToList();
        var projectIds = new HashSet<ProjectId>(projects.Select(p => p.Id));
        var assigned = new Dictionary<ProjectId, int>();
        var visiting = new HashSet<ProjectId>();

        int GetLevel(Project project)
        {
            if (assigned.TryGetValue(project.Id, out var cached))
                return cached;

            if (!visiting.Add(project.Id))
                return 0;

            var maxDep = -1;
            foreach (var dep in project.ProjectReferences)
            {
                if (!projectIds.Contains(dep.ProjectId))
                    continue;
                var depProject = solution.GetProject(dep.ProjectId);
                if (depProject != null)
                    maxDep = Math.Max(maxDep, GetLevel(depProject));
            }

            var level = maxDep + 1;
            assigned[project.Id] = level;
            visiting.Remove(project.Id);
            return level;
        }

        foreach (ref readonly var project in CollectionsMarshal.AsSpan(projects))
            GetLevel(project);

        if (assigned.Count == 0)
            return [];

        var maxLevel = assigned.Values.Max();
        var levels = new List<List<Project>>(maxLevel + 1);
        for (var i = 0; i <= maxLevel; i++)
            levels.Add(new List<Project>());

        foreach (ref readonly var project in CollectionsMarshal.AsSpan(projects))
            levels[assigned[project.Id]].Add(project);

        return levels;
    }
}
