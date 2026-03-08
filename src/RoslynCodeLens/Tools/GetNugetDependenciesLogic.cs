using System.Xml.Linq;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class GetNugetDependenciesLogic
{
    public static NugetDependencyGraph? Execute(LoadedSolution loaded, string? project)
    {
        var packages = new List<NugetDependency>();

        foreach (var proj in loaded.Solution.Projects)
        {
            if (project != null &&
                !proj.Name.Contains(project, StringComparison.OrdinalIgnoreCase))
                continue;

            if (proj.FilePath == null)
                continue;

            var doc = XDocument.Load(proj.FilePath);
            foreach (var pkgRef in doc.Descendants("PackageReference"))
            {
                var name = pkgRef.Attribute("Include")?.Value;
                var version = pkgRef.Attribute("Version")?.Value
                    ?? pkgRef.Element("Version")?.Value
                    ?? "*";

                if (name != null)
                    packages.Add(new NugetDependency(name, version, proj.Name));
            }
        }

        return new NugetDependencyGraph(packages);
    }
}
