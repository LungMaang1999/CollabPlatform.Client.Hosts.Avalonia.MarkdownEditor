namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// Specifies the parser behavior when creating or updating a document.
/// </summary>
public enum ParseMode
{
    /// <summary>
    /// Loads source content without marking the document as modified.
    /// </summary>
    Loading,

    /// <summary>
    /// Applies an editor change and marks the document source as modified.
    /// </summary>
    Editing
}