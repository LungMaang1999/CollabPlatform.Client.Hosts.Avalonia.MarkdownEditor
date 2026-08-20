namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>
/// Thrown when style inheritance cycle or invalid reference is detected.
/// </summary>
public sealed class StyleResolutionException : Exception
{
    public StyleResolutionException(string message) : base(message) { }
    public StyleResolutionException(string message, Exception innerException) : base(message, innerException) { }
}