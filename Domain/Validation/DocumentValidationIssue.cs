namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Validation;
/// <summary>
/// Describes a validation anomaly within the document AST model.
/// </summary>
public sealed record DocumentValidationIssue(
    DocumentValidationSeverity Severity,
    string Code,
    string Message,
    string? NodeId = null);