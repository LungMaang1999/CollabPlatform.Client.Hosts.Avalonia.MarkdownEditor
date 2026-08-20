namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// Represents a contiguous text range and coordinate within the raw Markdown source code.
/// </summary>
public sealed record SourceRange
{
    public int StartOffset { get; init; }
    public int Length { get; init; }
    public int StartLine { get; init; }
    public int StartColumn { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }

    /// <summary>
    /// Computes the exclusive ending character offset in the source document.
    /// </summary>
    public int EndOffset => StartOffset + Length;

    public static SourceRange Empty() => new()
    {
        StartOffset = 0,
        Length = 0,
        StartLine = 1,
        StartColumn = 1,
        EndLine = 1,
        EndColumn = 1
    };
}