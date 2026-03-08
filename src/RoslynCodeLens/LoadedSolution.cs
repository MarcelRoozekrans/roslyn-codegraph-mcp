using Microsoft.CodeAnalysis;

namespace RoslynCodeLens;

public class LoadedSolution
{
    public required Solution Solution { get; init; }
    public required IDictionary<ProjectId, Compilation> Compilations { get; init; }
    public bool IsEmpty => Compilations.Count == 0;

    public static LoadedSolution Empty { get; } = CreateEmpty();

    private static LoadedSolution CreateEmpty()
    {
        var workspace = new AdhocWorkspace();
        return new LoadedSolution
        {
            Solution = workspace.CurrentSolution,
            Compilations = new Dictionary<ProjectId, Compilation>()
        };
    }
}
