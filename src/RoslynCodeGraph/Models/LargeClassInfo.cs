namespace RoslynCodeGraph.Models;

public record LargeClassInfo(string TypeName, int MemberCount, int LineCount, string File, int Line, string Project);
