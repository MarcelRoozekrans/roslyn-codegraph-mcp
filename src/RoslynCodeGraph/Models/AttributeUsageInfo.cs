namespace RoslynCodeGraph.Models;

public record AttributeUsageInfo(
    string AttributeName,
    string TargetKind,
    string TargetName,
    string File,
    int Line,
    string Project);
