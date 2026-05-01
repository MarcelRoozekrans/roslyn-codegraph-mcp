namespace RoslynCodeLens.Models;

public record OverloadParameter(
    string Name,
    string Type,
    bool IsOptional,
    string? DefaultValue,
    bool IsParams,
    string Modifier);
