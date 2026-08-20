using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Parsing;

public interface IMarkdownParser
{
    ParseResult Parse(string source, MarkdownDocument? previous = null, ParseMode mode = ParseMode.Editing);
}