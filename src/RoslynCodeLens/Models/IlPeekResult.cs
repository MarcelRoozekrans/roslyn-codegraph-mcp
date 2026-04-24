namespace RoslynCodeLens.Models;

public record IlPeekResult(
    string MethodFullName,
    string AssemblyName,
    string AssemblyVersion,
    string Il);
