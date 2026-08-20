using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;

public sealed record DocumentLoadResult(MarkdownDocument Document, DocumentFileSnapshot Snapshot);
