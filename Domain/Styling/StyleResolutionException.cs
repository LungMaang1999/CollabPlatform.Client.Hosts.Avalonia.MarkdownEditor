namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

public sealed class StyleResolutionException : Exception
{
    public StyleResolutionException(string message) : base(message) { }
    public StyleResolutionException(string message, Exception innerException) : base(message, innerException) { }
}
