namespace TestLib;

// Public record class — kind: Record, plus its positional properties (Id, Name) as kind: Property
public record OrderRecord(int Id, string Name);

// Public record struct — kind: RecordStruct, plus positional properties (X, Y)
public record struct PointStruct(int X, int Y);

// Public enum — kind: Enum
public enum PriorityLevel
{
    Low,
    High
}

// Public delegate — kind: Delegate
public delegate void OrderProcessedHandler(int orderId);

// Public abstract class — protected members ARE part of API (subclassable from outside)
public abstract class AbstractProcessor
{
    public void Run() { }
    protected abstract void Process();
    protected int Counter;
}

// Public sealed class — protected members are NOT reachable, must be excluded
public sealed class SealedHolder
{
    public int PublicProp { get; set; }
    protected void HiddenProtected() { }   // EXCLUDED — sealed type's protected is unreachable
}

// Public class with indexer + user-defined operator
public class IndexerSample
{
    public string this[int index] => $"Item {index}";

    public static IndexerSample operator +(IndexerSample a, IndexerSample b)
        => new IndexerSample();
}

// Internal type — entire type and its members must be excluded (not public API)
internal class InternalHidden
{
    public void NotApi() { }
}
