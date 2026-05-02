using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.TestDiscovery;

public static class TestFrameworkDetector
{
    public static TestFramework? DetectFramework(Project project)
    {
        if (project.FilePath is null || !File.Exists(project.FilePath))
            return null;

        var content = File.ReadAllText(project.FilePath);

        // Order matters: xUnit projects can transitively reference NUnit packages
        // in unusual setups, but the explicit `xunit` package pin is authoritative.
        if (content.Contains("PackageReference Include=\"xunit", StringComparison.OrdinalIgnoreCase))
            return TestFramework.XUnit;
        if (content.Contains("PackageReference Include=\"NUnit", StringComparison.OrdinalIgnoreCase))
            return TestFramework.NUnit;
        if (content.Contains("PackageReference Include=\"MSTest", StringComparison.OrdinalIgnoreCase))
            return TestFramework.MSTest;

        return null;
    }
}
