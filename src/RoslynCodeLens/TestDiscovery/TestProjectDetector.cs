using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.TestDiscovery;

public static class TestProjectDetector
{
    private static readonly string[] TestPackagePrefixes = ["xunit", "nunit", "mstest"];

    public static ImmutableHashSet<ProjectId> GetTestProjectIds(Solution solution)
    {
        var builder = ImmutableHashSet.CreateBuilder<ProjectId>();

        foreach (var project in solution.Projects)
        {
            if (HasTestPackageReference(project))
                builder.Add(project.Id);
        }

        return builder.ToImmutable();
    }

    private static bool HasTestPackageReference(Project project)
    {
        if (project.FilePath is null || !File.Exists(project.FilePath))
            return false;

        var content = File.ReadAllText(project.FilePath);

        // Look for <PackageReference Include="xunit..." or "NUnit..." or "MSTest..."
        foreach (var prefix in TestPackagePrefixes)
        {
            var needle = $"PackageReference Include=\"{prefix}";
            if (content.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
