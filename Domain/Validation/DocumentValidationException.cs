namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Validation;

/// <summary>
/// Thrown when document structure rules are violated.
/// </summary>
public sealed class DocumentValidationException : InvalidOperationException
{
    public DocumentValidationException(string message) : base(message) { }
}