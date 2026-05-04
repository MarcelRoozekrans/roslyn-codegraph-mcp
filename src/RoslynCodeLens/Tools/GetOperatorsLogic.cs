using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class GetOperatorsLogic
{
    private static readonly Dictionary<string, string> OperatorMap = new(StringComparer.Ordinal)
    {
        ["op_UnaryPlus"] = "+",
        ["op_UnaryNegation"] = "-",
        ["op_LogicalNot"] = "!",
        ["op_OnesComplement"] = "~",
        ["op_Increment"] = "++",
        ["op_Decrement"] = "--",
        ["op_True"] = "true",
        ["op_False"] = "false",
        ["op_Addition"] = "+",
        ["op_Subtraction"] = "-",
        ["op_Multiply"] = "*",
        ["op_Division"] = "/",
        ["op_Modulus"] = "%",
        ["op_BitwiseAnd"] = "&",
        ["op_BitwiseOr"] = "|",
        ["op_ExclusiveOr"] = "^",
        ["op_LeftShift"] = "<<",
        ["op_RightShift"] = ">>",
        ["op_UnsignedRightShift"] = ">>>",
        ["op_Equality"] = "==",
        ["op_Inequality"] = "!=",
        ["op_LessThan"] = "<",
        ["op_LessThanOrEqual"] = "<=",
        ["op_GreaterThan"] = ">",
        ["op_GreaterThanOrEqual"] = ">=",
        ["op_Implicit"] = "implicit",
        ["op_Explicit"] = "explicit",
    };

    private static readonly SymbolDisplayFormat SignatureFormat =
        SymbolDisplayFormat.CSharpShortErrorMessageFormat;

    public static GetOperatorsResult Execute(
        SymbolResolver resolver,
        MetadataSymbolResolver metadata,
        string symbol)
    {
        var containingType = ResolveContainingType(resolver, metadata, symbol);
        if (containingType is null)
            return new GetOperatorsResult(string.Empty, []);

        var operators = containingType
            .GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.UserDefinedOperator
                     || m.MethodKind == MethodKind.Conversion)
            .Select(BuildOperatorInfo)
            .ToList();

        operators.Sort((a, b) =>
        {
            var byKind = string.CompareOrdinal(a.Kind, b.Kind);
            if (byKind != 0) return byKind;
            var byArity = a.Parameters.Count.CompareTo(b.Parameters.Count);
            if (byArity != 0) return byArity;
            return string.CompareOrdinal(a.Signature, b.Signature);
        });

        return new GetOperatorsResult(
            ContainingType: containingType.ToDisplayString(),
            Operators: operators);
    }

    private static INamedTypeSymbol? ResolveContainingType(
        SymbolResolver resolver, MetadataSymbolResolver metadata, string symbol)
    {
        var sourceTypes = resolver.FindNamedTypes(symbol);
        if (sourceTypes.Count > 0)
            return sourceTypes[0];

        var resolved = metadata.Resolve(symbol);
        if (resolved?.Symbol is INamedTypeSymbol nt)
            return nt;

        return null;
    }

    private static OperatorInfo BuildOperatorInfo(IMethodSymbol method)
    {
        var location = method.Locations.FirstOrDefault(l => l.IsInSource);
        var (file, line) = (string.Empty, 0);
        if (location is not null)
        {
            var span = location.GetLineSpan();
            (file, line) = (span.Path, span.StartLinePosition.Line + 1);
        }

        var kind = KindFromMetadataName(method.MetadataName, out var isChecked);

        return new OperatorInfo(
            Kind: kind,
            Signature: method.ToDisplayString(SignatureFormat),
            ReturnType: method.ReturnType.ToDisplayString(),
            Parameters: method.Parameters.Select(MethodDisplayHelpers.BuildParameter).ToList(),
            Accessibility: MethodDisplayHelpers.AccessibilityToString(method.DeclaredAccessibility),
            IsCheckedVariant: isChecked,
            XmlDocSummary: MethodDisplayHelpers.ExtractSummary(method),
            FilePath: file,
            Line: line);
    }

    private static string KindFromMetadataName(string metadataName, out bool isChecked)
    {
        isChecked = false;
        if (OperatorMap.TryGetValue(metadataName, out var kind))
            return kind;

        if (metadataName.StartsWith("op_Checked", StringComparison.Ordinal))
        {
            var unchecked_ = "op_" + metadataName["op_Checked".Length..];
            if (OperatorMap.TryGetValue(unchecked_, out var baseKind))
            {
                isChecked = true;
                return baseKind;
            }
        }

        return metadataName;
    }
}
