namespace RoslynCodeGraph.Models;

public record CodeFixSuggestion(string Title, string DiagnosticId, IReadOnlyList<TextEdit> Edits);
