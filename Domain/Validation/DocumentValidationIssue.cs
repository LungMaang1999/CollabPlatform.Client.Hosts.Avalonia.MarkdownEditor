namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Validation;
public sealed record DocumentValidationIssue(
    DocumentValidationSeverity Severity,
    string Code,
    string Message,
    string? NodeId = null);
