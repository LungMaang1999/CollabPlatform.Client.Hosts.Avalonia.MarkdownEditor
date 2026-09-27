using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;

public sealed class DiagnosticMessage
{
    public DiagnosticSeverity Severity { get; init; }

    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public SourceRange Range { get; init; } = SourceRange.Empty();
}
