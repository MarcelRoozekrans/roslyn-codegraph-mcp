namespace RoslynCodeGraph.Models;

public record TextEdit(string FilePath, int StartLine, int StartColumn, int EndLine, int EndColumn, string NewText);

public record CodeFixSuggestion(string Title, string DiagnosticId, List<TextEdit> Edits);
