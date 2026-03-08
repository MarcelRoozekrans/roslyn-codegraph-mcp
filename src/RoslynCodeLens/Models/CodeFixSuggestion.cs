namespace RoslynCodeLens.Models;

public record CodeFixSuggestion(string Title, string DiagnosticId, IReadOnlyList<TextEdit> Edits);
