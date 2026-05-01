using System;

namespace TestLib.ObsoleteSamples;

public class ObsoleteApi
{
    [Obsolete("Use NewWay instead")]
    public void ObsoleteWarning() { }

    [Obsolete]
    public void ObsoleteWithoutMessage() { }

    [Obsolete("Should not appear")]
    public void UnusedObsolete() { }
}

[Obsolete("Drop this type")]
public class ObsoleteType
{
    public void Bar() { }
}

// Error-level deprecation marker. Referenced ONLY via nameof() below — calling it
// directly would emit CS0619 (which Roslyn surfaces in Compilation.GetDiagnostics()
// even when wrapped in #pragma warning disable). nameof() doesn't trigger the
// obsolete diagnostic but still produces a syntactic reference that find_obsolete_usage
// counts as a usage.
[Obsolete("Hard fail", true)]
public class ObsoleteErrorTypeMarker
{
    public void DoNotCall() { }
}

public class ObsoleteConsumer
{
    private readonly ObsoleteApi _api = new();

    public void UseAll()
    {
        _api.ObsoleteWarning();
        _api.ObsoleteWarning();
        _api.ObsoleteWithoutMessage();
        // CS0619 from this nameof reference is suppressed via NoWarn in TestLib.csproj.
        var errorMarker = nameof(ObsoleteErrorTypeMarker);
    }

    public void UseObsoleteType()
    {
        var t = new ObsoleteType();
        var name = nameof(ObsoleteType);
    }

    public void UseConditionalAccess()
    {
        // Conditional access path: obj?.Member() — caught reviewer-flagged overcounting via MemberBindingExpressionSyntax.
        ObsoleteApi? maybe = _api;
        maybe?.ObsoleteWarning();
    }

    public void UseQualifiedNew()
    {
        // Qualified-name new: new Ns.Type() — caught reviewer-flagged overcounting via QualifiedNameSyntax.
        var t = new TestLib.ObsoleteSamples.ObsoleteType();
    }
}
