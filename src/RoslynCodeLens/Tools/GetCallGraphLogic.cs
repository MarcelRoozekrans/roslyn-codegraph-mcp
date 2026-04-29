using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class GetCallGraphLogic
{
    private static readonly SymbolDisplayFormat FqnFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMemberOptions(
            SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeExplicitInterface)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType);

    public static async Task<GetCallGraphResult?> ExecuteAsync(
        LoadedSolution loaded,
        SymbolResolver resolver,
        MetadataSymbolResolver metadata,
        string symbol,
        string direction,
        int maxDepth,
        int maxNodes,
        CancellationToken cancellationToken = default)
    {
        if (direction is not ("callees" or "callers" or "both"))
            throw new ArgumentException(
                $"Invalid direction '{direction}'. Expected 'callees', 'callers', or 'both'.",
                nameof(direction));

        var methods = resolver.FindMethods(symbol);
        if (methods.Count == 0) return null;

        var root = methods[0];
        var rootFqn = Fqn(root);

        var treeToCompilation = new Dictionary<SyntaxTree, Compilation>();
        foreach (var (_, comp) in loaded.Compilations)
            foreach (var tree in comp.SyntaxTrees)
                treeToCompilation[tree] = comp;

        var callees = new Dictionary<string, CallGraphNode>(StringComparer.Ordinal);
        var callers = new Dictionary<string, CallGraphNode>(StringComparer.Ordinal);
        var truncated = false;

        if (direction is "callees" or "both")
            truncated |= WalkCallees(loaded, treeToCompilation, resolver, root, rootFqn, maxDepth, maxNodes, cancellationToken, callees);

        if (direction is "callers" or "both")
            truncated |= await WalkCallersAsync(loaded, resolver, root, rootFqn, maxDepth, maxNodes, callers, cancellationToken).ConfigureAwait(false);

        return new GetCallGraphResult(
            Root: rootFqn,
            Direction: direction,
            MaxDepthRequested: maxDepth,
            Truncated: truncated,
            Callees: callees,
            Callers: callers);
    }

    private static bool WalkCallees(
        LoadedSolution loaded,
        IReadOnlyDictionary<SyntaxTree, Compilation> treeToCompilation,
        SymbolResolver resolver,
        IMethodSymbol root,
        string rootFqn,
        int maxDepth,
        int maxNodes,
        CancellationToken cancellationToken,
        Dictionary<string, CallGraphNode> map)
    {
        var queue = new Queue<(IMethodSymbol Sym, int Depth)>();
        queue.Enqueue((root, 0));
        map[rootFqn] = BuildNode(resolver, root, edges: []);

        var truncated = false;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            var currentFqn = Fqn(current);
            var edges = new List<CallGraphEdge>();

            foreach (var (callee, edgeKind) in EnumerateOutgoingCalls(treeToCompilation, current, cancellationToken))
            {
                var calleeFqn = Fqn(callee);

                if (!map.ContainsKey(calleeFqn))
                {
                    if (map.Count >= maxNodes)
                    {
                        truncated = true;
                        // edge to truncated target still recorded below
                    }
                    else
                    {
                        var isExternal = !callee.Locations.Any(l => l.IsInSource);
                        map[calleeFqn] = BuildNode(resolver, callee, edges: [], forceExternal: isExternal);

                        if (!isExternal)
                            queue.Enqueue((callee, depth + 1));
                    }
                }

                edges.Add(new CallGraphEdge(calleeFqn, edgeKind));
            }

            map[currentFqn] = map[currentFqn] with { Edges = edges };
        }

        return truncated;
    }

    private static async Task<bool> WalkCallersAsync(
        LoadedSolution loaded,
        SymbolResolver resolver,
        IMethodSymbol root,
        string rootFqn,
        int maxDepth,
        int maxNodes,
        Dictionary<string, CallGraphNode> map,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<(IMethodSymbol Sym, int Depth)>();
        queue.Enqueue((root, 0));
        map[rootFqn] = BuildNode(resolver, root, edges: []);

        var truncated = false;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            var currentFqn = Fqn(current);
            var edges = new List<CallGraphEdge>();

            var callerInfos = await SymbolFinder.FindCallersAsync(current, loaded.Solution, cancellationToken)
                .ConfigureAwait(false);

            foreach (var info in callerInfos)
            {
                if (info.CallingSymbol is not IMethodSymbol caller) continue;

                var callerFqn = Fqn(caller);
                if (!map.ContainsKey(callerFqn))
                {
                    if (map.Count >= maxNodes)
                    {
                        truncated = true;
                        // edge to truncated target still recorded below
                    }
                    else
                    {
                        map[callerFqn] = BuildNode(resolver, caller, edges: []);
                        queue.Enqueue((caller, depth + 1));
                    }
                }

                edges.Add(new CallGraphEdge(callerFqn, EdgeKindFor(caller)));
            }

            map[currentFqn] = map[currentFqn] with { Edges = edges };
        }

        return truncated;
    }

    private static IEnumerable<(IMethodSymbol Callee, CallGraphEdgeKind EdgeKind)> EnumerateOutgoingCalls(
        IReadOnlyDictionary<SyntaxTree, Compilation> treeToCompilation,
        IMethodSymbol method,
        CancellationToken cancellationToken)
    {
        var location = method.Locations.FirstOrDefault(l => l.IsInSource);
        if (location?.SourceTree is null) yield break;

        if (!treeToCompilation.TryGetValue(location.SourceTree, out var compilation))
            yield break;

        var semanticModel = compilation.GetSemanticModel(location.SourceTree);
        var bodyNode = location.SourceTree.GetRoot().FindNode(location.SourceSpan);

        var seen = new HashSet<(string, CallGraphEdgeKind)>();

        foreach (var node in bodyNode.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();

            IMethodSymbol? called = null;
            CallGraphEdgeKind kind = CallGraphEdgeKind.Method;

            switch (node)
            {
                case InvocationExpressionSyntax inv:
                    if (semanticModel.GetSymbolInfo(inv).Symbol is IMethodSymbol m)
                    {
                        called = m;
                        kind = m.MethodKind == MethodKind.UserDefinedOperator
                            ? CallGraphEdgeKind.Operator
                            : CallGraphEdgeKind.Method;
                    }
                    break;

                case ObjectCreationExpressionSyntax oc:
                    if (semanticModel.GetSymbolInfo(oc).Symbol is IMethodSymbol ctor)
                    {
                        called = ctor;
                        kind = CallGraphEdgeKind.Constructor;
                    }
                    break;

                case AssignmentExpressionSyntax asg:
                    if (semanticModel.GetSymbolInfo(asg.Left).Symbol is IPropertySymbol setProp
                        && setProp.SetMethod is IMethodSymbol setter)
                    {
                        called = setter;
                        kind = CallGraphEdgeKind.PropertySet;
                    }
                    break;

                case MemberAccessExpressionSyntax ma:
                    if (semanticModel.GetSymbolInfo(ma).Symbol is IPropertySymbol getProp
                        && !IsPropertyWriteContext(ma)
                        && getProp.GetMethod is IMethodSymbol getter)
                    {
                        called = getter;
                        kind = CallGraphEdgeKind.PropertyGet;
                    }
                    break;

                case BinaryExpressionSyntax bin:
                    if (semanticModel.GetSymbolInfo(bin).Symbol is IMethodSymbol op
                        && op.MethodKind == MethodKind.UserDefinedOperator)
                    {
                        called = op;
                        kind = CallGraphEdgeKind.Operator;
                    }
                    break;
            }

            if (called is null) continue;
            var dedupKey = (Fqn(called), kind);
            if (!seen.Add(dedupKey)) continue;
            yield return (called, kind);
        }
    }

    private static bool IsPropertyWriteContext(MemberAccessExpressionSyntax ma)
        => ma.Parent is AssignmentExpressionSyntax asg && asg.Left == ma;

    private static CallGraphNode BuildNode(
        SymbolResolver resolver,
        IMethodSymbol symbol,
        IReadOnlyList<CallGraphEdge> edges,
        bool forceExternal = false)
    {
        var isExternal = forceExternal || !symbol.Locations.Any(l => l.IsInSource);
        var (file, line) = isExternal ? ("", 0) : resolver.GetFileAndLine(symbol);
        var project = isExternal ? "" : resolver.GetProjectName(symbol);

        return new CallGraphNode(
            Kind: NodeKindFor(symbol),
            Project: project,
            FilePath: file,
            Line: line,
            IsExternal: isExternal,
            Edges: edges);
    }

    private static CallGraphNodeKind NodeKindFor(IMethodSymbol symbol)
        => symbol.MethodKind switch
        {
            MethodKind.Constructor => CallGraphNodeKind.Constructor,
            MethodKind.UserDefinedOperator or MethodKind.Conversion => CallGraphNodeKind.Operator,
            MethodKind.PropertyGet or MethodKind.PropertySet => CallGraphNodeKind.Property,
            _ => CallGraphNodeKind.Method
        };

    private static CallGraphEdgeKind EdgeKindFor(IMethodSymbol symbol)
        => symbol.MethodKind switch
        {
            MethodKind.Constructor => CallGraphEdgeKind.Constructor,
            MethodKind.UserDefinedOperator or MethodKind.Conversion => CallGraphEdgeKind.Operator,
            MethodKind.PropertyGet => CallGraphEdgeKind.PropertyGet,
            MethodKind.PropertySet => CallGraphEdgeKind.PropertySet,
            _ => CallGraphEdgeKind.Method
        };

    private static string Fqn(ISymbol symbol)
        => symbol.ToDisplayString(FqnFormat).Replace("global::", string.Empty, StringComparison.Ordinal);
}
