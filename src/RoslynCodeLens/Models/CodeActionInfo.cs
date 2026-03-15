namespace RoslynCodeLens.Models;

public record CodeActionInfo(string Title, string Kind, IReadOnlyList<CodeActionInfo>? NestedActions = null);
