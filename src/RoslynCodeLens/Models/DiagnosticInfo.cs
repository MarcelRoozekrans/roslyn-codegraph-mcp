namespace RoslynCodeLens.Models;

public record DiagnosticInfo(string Id, string Severity, string Message, string File, int Line, string Project, string Source = "compiler");
