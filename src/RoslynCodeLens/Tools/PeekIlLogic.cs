using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.Metadata;
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Metadata;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class PeekIlLogic
{
    public static IlPeekResult Execute(
        LoadedSolution loaded,
        MetadataSymbolResolver metadata,
        IlDisassemblerAdapter disassembler,
        string methodSymbol)
    {
        var method = ResolveMethod(loaded, metadata, methodSymbol);

        var assembly = method.ContainingAssembly
            ?? throw new ArgumentException($"Could not determine containing assembly for '{methodSymbol}'.", nameof(methodSymbol));

        var assemblyPath = FindAssemblyPath(loaded, assembly)
            ?? throw new ArgumentException(
                $"Could not locate on-disk path for assembly '{assembly.Identity.Name}'.", nameof(methodSymbol));

        // Try fast reflection-based token first; fall back to PE metadata scan
        var token = MetadataTokenOf(method)
            ?? MetadataTokenFromPE(disassembler.Cache, assemblyPath, method)
            ?? throw new ArgumentException(
                $"Could not determine metadata token for '{methodSymbol}'.", nameof(methodSymbol));

        var ilText = disassembler.DisassembleMethod(assemblyPath, token);

        return new IlPeekResult(
            method.ToDisplayString(),
            assembly.Identity.Name,
            assembly.Identity.Version.ToString(),
            ilText);
    }

    private static IMethodSymbol ResolveMethod(
        LoadedSolution loaded,
        MetadataSymbolResolver metadata,
        string methodSymbol)
    {
        // First try the existing metadata resolver (works for simple member names)
        var resolved = metadata.Resolve(methodSymbol);
        if (resolved != null)
        {
            ValidateResolved(resolved, methodSymbol);
            return (IMethodSymbol)resolved.Symbol;
        }

        // Fall back to direct PE-metadata lookup for fully-qualified signatures
        // like "Ns.Type..ctor(T1, T2)" or "Ns.Type.Method(T1, T2)"
        var method = ResolveBySignature(loaded, metadata, methodSymbol)
            ?? throw new ArgumentException(
                $"Symbol '{methodSymbol}' not found. Pass a fully-qualified method name with parameter types.", nameof(methodSymbol));

        return method;
    }

    private static void ValidateResolved(ResolvedSymbol resolved, string methodSymbol)
    {
        if (!string.Equals(resolved.Origin.Kind, "metadata", StringComparison.Ordinal))
            throw new ArgumentException(
                $"'{methodSymbol}' is a source symbol. Use go_to_definition to navigate to source.", nameof(methodSymbol));

        if (resolved.Symbol is not IMethodSymbol method)
            throw new ArgumentException(
                $"'{methodSymbol}' is not a method. peek_il only works on methods.", nameof(methodSymbol));

        if (method.IsAbstract ||
            (method.ContainingType?.TypeKind == TypeKind.Interface && !method.IsStatic))
            throw new ArgumentException(
                $"'{methodSymbol}' has no body (abstract or interface instance member).", nameof(methodSymbol));
    }

    private static IMethodSymbol? ResolveBySignature(
        LoadedSolution loaded,
        MetadataSymbolResolver metadata,
        string methodSymbol)
    {
        // Parse "Namespace.Type.MethodOrCtor(ParamType1, ParamType2)"
        // or    "Namespace.Type..ctor(ParamType1, ParamType2)"
        var parenIdx = methodSymbol.IndexOf('(', StringComparison.Ordinal);
        var nameWithoutParams = parenIdx >= 0 ? methodSymbol[..parenIdx] : methodSymbol;
        string? paramSignature = parenIdx >= 0 ? methodSymbol[parenIdx..] : null;

        // Split type vs member name; handle ".ctor" double-dot
        string typeName;
        string memberName;

        var ctorIdx = nameWithoutParams.LastIndexOf("..ctor", StringComparison.Ordinal);
        if (ctorIdx >= 0)
        {
            typeName = nameWithoutParams[..ctorIdx];
            memberName = ".ctor";
        }
        else
        {
            var lastDot = nameWithoutParams.LastIndexOf('.');
            if (lastDot <= 0)
                return null;
            typeName = nameWithoutParams[..lastDot];
            memberName = nameWithoutParams[(lastDot + 1)..];
        }

        // Look up the type in all compilations
        foreach (var compilation in loaded.Compilations.Values)
        {
            var container = compilation.GetTypeByMetadataName(typeName);
            if (container == null || container.Locations.Any(l => l.IsInSource))
                continue;

            var candidates = container.GetMembers(memberName).OfType<IMethodSymbol>().ToList();
            if (candidates.Count == 0)
                continue;

            // If no param signature, return the first overload
            if (paramSignature == null || candidates.Count == 1)
            {
                var m = candidates[0];
                ValidateMethodBody(m, methodSymbol);
                return m;
            }

            // Match by display-string suffix or by param count
            foreach (var candidate in candidates)
            {
                var display = candidate.ToDisplayString();
                if (display.Contains(paramSignature, StringComparison.OrdinalIgnoreCase))
                {
                    ValidateMethodBody(candidate, methodSymbol);
                    return candidate;
                }
            }

            // Fallback: match by parameter count from the signature string
            var paramTypes = ParseParamTypes(paramSignature!.TrimStart('(').TrimEnd(')'));
            var byCount = candidates.Where(c => c.Parameters.Length == paramTypes.Count).ToList();
            if (byCount.Count == 1)
            {
                ValidateMethodBody(byCount[0], methodSymbol);
                return byCount[0];
            }
        }

        return null;
    }

    private static void ValidateMethodBody(IMethodSymbol method, string methodSymbol)
    {
        if (method.Locations.Any(l => l.IsInSource))
            throw new ArgumentException(
                $"'{methodSymbol}' is a source symbol. Use go_to_definition to navigate to source.", nameof(methodSymbol));

        if (method.IsAbstract ||
            (method.ContainingType?.TypeKind == TypeKind.Interface && !method.IsStatic))
            throw new ArgumentException(
                $"'{methodSymbol}' has no body (abstract or interface instance member).", nameof(methodSymbol));
    }

    private static List<string> ParseParamTypes(string paramList)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(paramList))
            return result;

        var depth = 0;
        var start = 0;
        for (var i = 0; i < paramList.Length; i++)
        {
            var c = paramList[i];
            if (c is '<' or '(') depth++;
            else if (c is '>' or ')') depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(paramList[start..i].Trim());
                start = i + 1;
            }
        }
        var last = paramList[start..].Trim();
        if (last.Length > 0)
            result.Add(last);
        return result;
    }

    // Returns first match; in multi-TFM solutions the same assembly may appear under multiple TFMs.
    // Non-deterministic order is acceptable since IL is identical across TFM-specific metadata references.
    private static string? FindAssemblyPath(LoadedSolution loaded, IAssemblySymbol assembly)
    {
        foreach (var compilation in loaded.Compilations.Values)
        {
            foreach (var reference in compilation.References)
            {
                if (reference is not PortableExecutableReference pe)
                    continue;
                var refAsm = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
                if (refAsm != null && SymbolEqualityComparer.Default.Equals(refAsm, assembly))
                    return pe.FilePath;
            }
        }
        return null;
    }

    private static int? MetadataTokenOf(IMethodSymbol method)
    {
        var prop = method.GetType().GetProperty("MetadataToken",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance);
        if (prop?.GetValue(method) is int token && token != 0)
            return token;
        return null;
    }

    /// <summary>
    /// Fallback: walk the PE metadata to find a method handle matching the Roslyn symbol
    /// by type name + method name + parameter names. Returns its raw metadata token.
    /// </summary>
    private static int? MetadataTokenFromPE(PEFileCache cache, string assemblyPath, IMethodSymbol method)
    {
        var pe = cache.Get(assemblyPath);
        var reader = pe.Metadata;

        var typeName = method.ContainingType?.MetadataName ?? "";
        var ns = method.ContainingNamespace?.ToDisplayString() ?? "";
        var roslynParamNames = method.Parameters.Select(p => p.Name).ToArray();
        var roslynParamCount = method.Parameters.Length;

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var typeDef = reader.GetTypeDefinition(typeHandle);
            if (!string.Equals(reader.GetString(typeDef.Name), typeName, StringComparison.Ordinal))
                continue;
            if (!string.IsNullOrEmpty(ns) &&
                !string.Equals(reader.GetString(typeDef.Namespace), ns, StringComparison.Ordinal))
                continue;

            var candidates = new List<MethodDefinitionHandle>();
            foreach (var methodHandle in typeDef.GetMethods())
            {
                var methodDef = reader.GetMethodDefinition(methodHandle);
                var name = reader.GetString(methodDef.Name);
                if (!string.Equals(name, method.Name, StringComparison.Ordinal))
                    continue;

                // Count non-return params (sequenceNumber > 0)
                var peParams = methodDef.GetParameters()
                    .Select(ph => reader.GetParameter(ph))
                    .Where(p => p.SequenceNumber > 0)
                    .ToList();

                if (peParams.Count != roslynParamCount)
                    continue;

                candidates.Add(methodHandle);
            }

            if (candidates.Count == 0)
                return null;

            if (candidates.Count == 1)
                return MetadataTokens.GetToken(candidates[0]);

            // Multiple overloads with same param count — match by parameter names
            foreach (var methodHandle in candidates)
            {
                var methodDef = reader.GetMethodDefinition(methodHandle);
                var peParamNames = methodDef.GetParameters()
                    .Select(ph => reader.GetParameter(ph))
                    .Where(p => p.SequenceNumber > 0)
                    .Select(p => reader.GetString(p.Name))
                    .ToArray();

                if (roslynParamNames.SequenceEqual(peParamNames, StringComparer.Ordinal))
                    return MetadataTokens.GetToken(methodHandle);
            }

            // Last resort: return first candidate if only name/count mismatch remains
            return MetadataTokens.GetToken(candidates[0]);
        }

        return null;
    }
}
