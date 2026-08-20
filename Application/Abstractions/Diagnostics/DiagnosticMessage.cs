using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;

/// <summary>
/// Represents a parser or renderer diagnostic.
/// </summary>
public sealed class DiagnosticMessage
{
    /// <summary>
    /// Gets the diagnostic severity.
    /// </summary>
    public DiagnosticSeverity Severity { get; init; }

    /// <summary>
    /// Gets the stable diagnostic code.
    /// </summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>
    /// Gets the human-readable diagnostic message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Gets the source range associated with the diagnostic.
    /// </summary>
    public SourceRange Range { get; init; } = SourceRange.Empty();
}