using System;
using System.IO;
using System.Threading.Tasks;

namespace DisposableFixture;

public class Misuse
{
    private MemoryStream? _heldField;

    // ============ POSITIVE CASES ============

    // Pattern 1 (DisposableNotDisposed): local declaration not disposed (constructor)
    public void NotDisposedFromConstructor()
    {
        var stream = new MemoryStream();
        stream.WriteByte(1);
    }

    // Pattern 1 (DisposableNotDisposed): local declaration not disposed (factory)
    public void NotDisposedFromFactory()
    {
        var stream = OpenStream();
        stream.WriteByte(1);
    }

    // Pattern 2 (DisposableDiscarded): bare-expression-statement constructor
    public void DiscardedConstructor()
    {
        new MemoryStream();
    }

    // Pattern 2 (DisposableDiscarded): bare-expression-statement factory
    public void DiscardedFactory()
    {
        OpenStream();
    }

    // ============ NEGATIVE CASES (must NOT be flagged) ============

    public void DisposedViaUsingDeclaration()
    {
        using var stream = new MemoryStream();
        stream.WriteByte(1);
    }

    public void DisposedViaUsingStatement()
    {
        using (var stream = new MemoryStream())
        {
            stream.WriteByte(1);
        }
    }

    public async Task DisposedViaAwaitUsing()
    {
        await using var stream = new MemoryStream();
        await stream.WriteAsync(new byte[] { 1 });
    }

    public Stream ReturnedToCaller()
    {
        var stream = new MemoryStream();
        return stream;
    }

    public void StoredInField()
    {
        var stream = new MemoryStream();
        _heldField = stream;
    }

    public void StoredInOutParam(out Stream result)
    {
        var stream = new MemoryStream();
        result = stream;
    }

    public void DiscardWithUnderscore()
    {
        _ = new MemoryStream();
    }

    private static MemoryStream OpenStream() => new MemoryStream();
}
