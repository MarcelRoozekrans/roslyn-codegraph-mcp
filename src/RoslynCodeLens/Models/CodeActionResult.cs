namespace RoslynCodeLens.Models;

public record CodeActionResult(
    bool Success,
    string Title,
    IReadOnlyList<TextEdit> Edits,
    string? ErrorMessage = null);
