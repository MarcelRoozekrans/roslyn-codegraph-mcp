namespace RoslynCodeLens.Models;

public enum PublicApiKind
{
    // Types
    Class,
    Struct,
    Interface,
    Enum,
    Record,
    RecordStruct,
    Delegate,
    // Members
    Constructor,
    Method,
    Property,
    Indexer,
    Field,
    Event,
    Operator
}
