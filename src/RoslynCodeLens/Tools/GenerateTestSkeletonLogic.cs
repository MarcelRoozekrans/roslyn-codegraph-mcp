using System.Text;
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class GenerateTestSkeletonLogic
{
    public static GenerateTestSkeletonResult Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string symbol,
        string? framework = null)
    {
        var symbols = resolver.FindSymbols(symbol);
        if (symbols.Count == 0)
            throw new InvalidOperationException($"Symbol not found: {symbol}");

        var first = symbols[0];
        INamedTypeSymbol targetType;
        List<IMethodSymbol> targetMethods;
        switch (first)
        {
            case INamedTypeSymbol type:
                targetType = type;
                targetMethods = EnumerateEligibleMethods(type).ToList();
                break;
            case IMethodSymbol method:
                targetType = method.ContainingType;
                targetMethods = new List<IMethodSymbol> { method };
                break;
            default:
                throw new InvalidOperationException(
                    $"Symbol must be a type or method, got {first.Kind}: {symbol}");
        }

        var fw = ResolveFramework(loaded, framework);
        var todoNotes = new List<string>();

        var className = $"{targetType.Name}Tests";
        var ns = $"{targetType.ContainingNamespace.ToDisplayString()}.Tests";

        var code = BuildClass(targetType, targetMethods, className, ns, fw, todoNotes);

        var suggestedPath = SuggestFilePath(loaded, targetType, todoNotes);

        return new GenerateTestSkeletonResult(
            Framework: fw.ToString(),
            SuggestedFilePath: suggestedPath,
            ClassName: className,
            Code: code,
            TodoNotes: todoNotes);
    }

    private static IEnumerable<IMethodSymbol> EnumerateEligibleMethods(INamedTypeSymbol type)
    {
        foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (m.MethodKind != MethodKind.Ordinary) continue;
            if (m.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected))
                continue;
            yield return m;
        }
    }

    private static TestFramework ResolveFramework(LoadedSolution loaded, string? overrideName)
    {
        if (overrideName is not null)
        {
            return overrideName.ToLowerInvariant() switch
            {
                "xunit" => TestFramework.XUnit,
                "nunit" => TestFramework.NUnit,
                "mstest" => TestFramework.MSTest,
                _ => throw new InvalidOperationException(
                    $"Unknown framework override '{overrideName}'. Use xunit, nunit, or mstest."),
            };
        }

        var counts = new Dictionary<TestFramework, int>();
        foreach (var p in loaded.Solution.Projects)
        {
            var detected = TestFrameworkDetector.DetectFramework(p);
            if (detected is null) continue;
            counts.TryGetValue(detected.Value, out var n);
            counts[detected.Value] = n + 1;
        }

        if (counts.Count == 0) return TestFramework.XUnit;

        TestFramework best = TestFramework.XUnit;
        int bestCount = -1;
        foreach (var kv in counts)
        {
            if (kv.Value > bestCount || (kv.Value == bestCount && kv.Key < best))
            {
                best = kv.Key;
                bestCount = kv.Value;
            }
        }
        return best;
    }

    private static string BuildClass(
        INamedTypeSymbol targetType,
        IReadOnlyList<IMethodSymbol> methods,
        string className,
        string ns,
        TestFramework fw,
        List<string> todoNotes)
    {
        var sb = new StringBuilder();

        // Usings
        var prodNs = targetType.ContainingNamespace.ToDisplayString();
        var hasAsync = methods.Any(ReturnsTask);

        sb.AppendLine($"using {prodNs};");
        if (hasAsync) sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine(FrameworkUsing(fw));
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"public class {className}");
        sb.AppendLine("{");

        if (methods.Count == 0)
        {
            sb.AppendLine("    // TODO: no public methods detected on " + targetType.Name);
            todoNotes.Add($"{targetType.Name} has no eligible public methods.");
        }

        for (int i = 0; i < methods.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            EmitMethodStub(sb, targetType, methods[i], fw, todoNotes);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string FrameworkUsing(TestFramework fw) => fw switch
    {
        TestFramework.XUnit => "using Xunit;",
        TestFramework.NUnit => "using NUnit.Framework;",
        TestFramework.MSTest => "using Microsoft.VisualStudio.TestTools.UnitTesting;",
        _ => "",
    };

    private static void EmitMethodStub(
        StringBuilder sb,
        INamedTypeSymbol targetType,
        IMethodSymbol method,
        TestFramework fw,
        List<string> todoNotes)
    {
        var factAttr = fw switch
        {
            TestFramework.XUnit => "[Fact]",
            TestFramework.NUnit => "[Test]",
            TestFramework.MSTest => "[TestMethod]",
            _ => "[Fact]",
        };

        var isAsync = ReturnsTask(method);
        var returnType = isAsync ? "async Task" : "void";
        var awaitable = isAsync ? "await " : "";

        sb.AppendLine($"    {factAttr}");
        sb.AppendLine($"    public {returnType} {method.Name}_HappyPath()");
        sb.AppendLine("    {");

        if (method.IsStatic)
        {
            sb.AppendLine($"        // TODO: arrange inputs");
            sb.AppendLine($"        {awaitable}{targetType.Name}.{method.Name}();");
        }
        else
        {
            sb.AppendLine($"        var sut = new {targetType.Name}();");
            sb.AppendLine($"        {awaitable}sut.{method.Name}();");
        }

        sb.AppendLine("        // TODO: assert");
        sb.AppendLine("    }");
    }

    private static bool ReturnsTask(IMethodSymbol method)
    {
        var rt = method.ReturnType;
        if (rt is null) return false;
        var name = rt.Name;
        if (string.Equals(name, "Task", StringComparison.Ordinal) ||
            string.Equals(name, "ValueTask", StringComparison.Ordinal))
        {
            return string.Equals(
                rt.ContainingNamespace?.ToDisplayString(),
                "System.Threading.Tasks",
                StringComparison.Ordinal);
        }
        return false;
    }

    private static string SuggestFilePath(
        LoadedSolution loaded,
        INamedTypeSymbol targetType,
        List<string> todoNotes)
    {
        var prodProject = loaded.Solution.Projects
            .FirstOrDefault(p => ContainsType(p, targetType));

        if (prodProject is null)
        {
            todoNotes.Add($"Could not locate production project for {targetType.Name}; using placeholder path.");
            return $"tests/{targetType.Name}Tests.cs";
        }

        var prodProjectName = prodProject.Name;
        var testProject = loaded.Solution.Projects
            .Where(p => p.ProjectReferences.Any(r => r.ProjectId == prodProject.Id))
            .Where(p => TestFrameworkDetector.DetectFramework(p) is not null)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        if (testProject is null)
        {
            todoNotes.Add($"No test project references {prodProjectName}; using placeholder path.");
            return $"tests/{prodProjectName}.Tests/{targetType.Name}Tests.cs";
        }

        return $"tests/{testProject.Name}/{targetType.Name}Tests.cs";
    }

    private static bool ContainsType(Project project, INamedTypeSymbol type)
    {
        foreach (var loc in type.Locations)
        {
            if (!loc.IsInSource || loc.SourceTree is null) continue;
            if (project.Documents.Any(d => d.FilePath == loc.SourceTree.FilePath))
                return true;
        }
        return false;
    }
}
