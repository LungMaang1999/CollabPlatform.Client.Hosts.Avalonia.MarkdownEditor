using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;

public sealed record RenderResult(string Html, IReadOnlyList<DiagnosticMessage> Diagnostics);