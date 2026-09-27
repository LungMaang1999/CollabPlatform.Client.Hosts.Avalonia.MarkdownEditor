namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Validation;

public sealed class DocumentValidationException : InvalidOperationException
{
    public DocumentValidationException(string message) : base(message) { }
}
