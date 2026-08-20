using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;

public interface IMarkdownSourceEditor
{
    string ChangeHeadingLevel(string source, SourceRange headingRange, int newLevel);
    string DeleteRange(string source, SourceRange range);
    string MoveBlock(string source, SourceRange range, int targetOffset);
    string ReplaceRange(string source, SourceRange range, string replacement);
}