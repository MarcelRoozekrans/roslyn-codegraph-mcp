using System.Globalization;
using System.Xml;
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class GetOverloadsLogic
{
    private static readonly SymbolDisplayFormat SignatureFormat =
        SymbolDisplayFormat.CSharpShortErrorMessageFormat;

    public static GetOverloadsResult Execute(
        SymbolResolver resolver,
        MetadataSymbolResolver metadata,
        string symbol)
    {
        var (containingType, methodName) = ResolveContainingTypeAndName(resolver, metadata, symbol);
        if (containingType is null || string.IsNullOrEmpty(methodName))
            return new GetOverloadsResult(string.Empty, []);

        var overloads = containingType
            .GetMembers(methodName)
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind != MethodKind.UserDefinedOperator
                     && m.MethodKind != MethodKind.Conversion)
            .Select(BuildOverloadInfo)
            .ToList();

        overloads.Sort((a, b) =>
        {
            var byCount = a.Parameters.Count.CompareTo(b.Parameters.Count);
            if (byCount != 0) return byCount;
            return string.CompareOrdinal(a.Signature, b.Signature);
        });

        return new GetOverloadsResult(
            ContainingType: containingType.ToDisplayString(),
            Overloads: overloads);
    }

    private static (INamedTypeSymbol? Type, string Name) ResolveContainingTypeAndName(
        SymbolResolver resolver, MetadataSymbolResolver metadata, string symbol)
    {
        var parts = symbol.Split('.');
        if (parts.Length < 2) return (null, string.Empty);

        var lastSegment = parts[^1];
        var typeName = string.Join('.', parts[..^1]);
        var typeNameLastSegment = parts[^2];

        // Constructor case: Type.Type → resolve as ".ctor" on Type.
        var isConstructor = string.Equals(lastSegment, typeNameLastSegment, StringComparison.Ordinal);
        var methodName = isConstructor ? ".ctor" : lastSegment;

        // 1) Source path: SymbolResolver.FindMethods works for ordinary methods. For
        //    constructors, walk types directly because FindMethods uses the literal
        //    segment as the member name.
        if (!isConstructor)
        {
            var methods = resolver.FindMethods(symbol);
            if (methods.Count > 0)
                return (methods[0].ContainingType, methodName);
        }
        else
        {
            foreach (var type in resolver.FindNamedTypes(typeName))
                if (type.GetMembers(".ctor").OfType<IMethodSymbol>().Any())
                    return (type, methodName);
        }

        // 2) Metadata fallback.
        var resolved = metadata.Resolve(symbol);
        if (resolved?.Symbol is IMethodSymbol mm)
            return (mm.ContainingType, methodName);
        if (resolved?.Symbol is INamedTypeSymbol nt && isConstructor)
            return (nt, methodName);

        return (null, string.Empty);
    }

    private static OverloadInfo BuildOverloadInfo(IMethodSymbol method)
    {
        var location = method.Locations.FirstOrDefault(l => l.IsInSource);
        var (file, line) = location is not null
            ? (location.GetLineSpan().Path, location.GetLineSpan().StartLinePosition.Line + 1)
            : (string.Empty, 0);

        return new OverloadInfo(
            Signature: method.ToDisplayString(SignatureFormat),
            ReturnType: method.ReturnType.ToDisplayString(),
            Parameters: method.Parameters.Select(BuildParameter).ToList(),
            Accessibility: AccessibilityToString(method.DeclaredAccessibility),
            IsStatic: method.IsStatic,
            IsVirtual: method.IsVirtual,
            IsAbstract: method.IsAbstract,
            IsOverride: method.IsOverride,
            IsAsync: method.IsAsync,
            IsExtensionMethod: method.IsExtensionMethod,
            TypeParameters: method.TypeParameters.Select(t => t.Name).ToList(),
            XmlDocSummary: ExtractSummary(method),
            FilePath: file,
            Line: line);
    }

    private static OverloadParameter BuildParameter(IParameterSymbol p)
    {
        var defaultText = p.HasExplicitDefaultValue
            ? FormatDefault(p.ExplicitDefaultValue)
            : null;

        var modifier = p.RefKind switch
        {
            RefKind.Ref => "ref",
            RefKind.Out => "out",
            RefKind.In => "in",
            RefKind.RefReadOnlyParameter => "ref readonly",
            _ => string.Empty,
        };

        return new OverloadParameter(
            Name: p.Name,
            Type: p.Type.ToDisplayString(),
            IsOptional: p.IsOptional,
            DefaultValue: defaultText,
            IsParams: p.IsParams,
            Modifier: modifier);
    }

    private static string FormatDefault(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        char c => $"'{c}'",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null",
    };

    private static string AccessibilityToString(Accessibility a) => a switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.Private => "private",
        _ => "internal",
    };

    private static string? ExtractSummary(IMethodSymbol method)
    {
        var xml = method.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var summary = doc.SelectSingleNode("//summary");
            var text = summary?.InnerText.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
