namespace RoslynCodeLens.Models;

public record GodObjectInfo(
    string TypeName,
    int LineCount,
    int MemberCount,
    int FieldCount,
    int IncomingNamespaces,
    int OutgoingNamespaces,
    IReadOnlyList<string> SampleIncoming,
    IReadOnlyList<string> SampleOutgoing,
    string FilePath,
    int Line,
    string Project,
    int SizeAxesExceeded,
    int CouplingAxesExceeded);
