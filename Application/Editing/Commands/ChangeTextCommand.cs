using System;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

/// <summary>
/// 区间文本替换命令：只保留被替换的旧片段与新片段（O(改动量)），
/// 不保留整文档快照。打字路径每个防抖编辑产生一条，长会话内存占用
/// 与编辑总增量成正比而非与"文档大小×步数"成正比。
/// </summary>
public sealed class ChangeTextCommand : IEditorCommand, IEditorCommandState, IEditorCommandMemory
{
    private readonly MarkdownDocument _document;
    private readonly IMarkdownEditApplier _editApplier;
    private readonly IMarkdownSourceEditor _sourceEditor;
    private readonly SourceRange _range;
    private readonly string _oldText;
    private readonly string _newText;

    public string Description => "Change Markdown text";
    public bool HasChanges { get; private set; }

    public ChangeTextCommand(MarkdownDocument document, IMarkdownEditApplier editApplier, IMarkdownSourceEditor sourceEditor, SourceRange range, string? replacement)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(editApplier);
        ArgumentNullException.ThrowIfNull(sourceEditor);
        ArgumentNullException.ThrowIfNull(range);

        _document = document;
        _editApplier = editApplier;
        _sourceEditor = sourceEditor;
        _range = range;
        _newText = replacement ?? string.Empty;

        var source = document.SourceMarkdown ?? string.Empty;
        int start = Math.Clamp(range.StartOffset, 0, source.Length);
        int length = Math.Clamp(range.Length, 0, source.Length - start);
        _oldText = source.Substring(start, length);
    }

    public void Execute() => Apply(_oldText, _newText, _range.Length);

    public void Undo() => Apply(_newText, _oldText, _newText.Length);

    private void Apply(string expected, string replacement, int removeLength)
    {
        if (string.Equals(expected, replacement, StringComparison.Ordinal))
        {
            HasChanges = false;
            return;
        }

        var source = _document.SourceMarkdown ?? string.Empty;
        int start = Math.Clamp(_range.StartOffset, 0, source.Length);
        int length = Math.Clamp(removeLength, 0, source.Length - start);

        var effectiveRange = new SourceRange
        {
            StartOffset = start,
            Length = length,
        };

        var updated = _sourceEditor.ReplaceRange(source, effectiveRange, replacement);

        if (string.Equals(source, updated, StringComparison.Ordinal))
        {
            HasChanges = false;
            return;
        }

        _editApplier.Apply(_document, updated);
        HasChanges = true;
    }

    public long EstimatedBytes =>
        2L * (_oldText.Length + _newText.Length) + 256;
}
