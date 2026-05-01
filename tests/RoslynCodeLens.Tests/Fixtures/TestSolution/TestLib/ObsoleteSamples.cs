using System;

namespace TestLib.ObsoleteSamples;

public class ObsoleteApi
{
    [Obsolete("Use NewWay instead")]
    public void ObsoleteWarning() { }

    [Obsolete("Hard fail", true)]
    public void ObsoleteError() { }

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

public class ObsoleteConsumer
{
    private readonly ObsoleteApi _api = new();

#pragma warning disable CS0612 // Type or member is obsolete
#pragma warning disable CS0618 // Type or member is obsolete
    public void UseAll()
    {
        _api.ObsoleteWarning();
        _api.ObsoleteWarning();
        _api.ObsoleteError();
        _api.ObsoleteWithoutMessage();
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
#pragma warning restore CS0618
#pragma warning restore CS0612
}
