using System;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

/// <summary>
/// 增量文本替换命令：仅记录旧文本与新文本切片，执行精确局部反向还原。
/// </summary>
public sealed class TextReplaceCommand : IEditorCommand, IEditorCommandState
{
    private readonly MarkdownDocument _document;
    private readonly IMarkdownEditApplier _editApplier;
    private readonly SourceRange _range;
    private readonly string _oldText;
    private readonly string _newText;

    public string Description { get; }
    public bool HasChanges { get; private set; }

    public TextReplaceCommand(
        MarkdownDocument document,
        IMarkdownEditApplier editApplier,
        SourceRange range,
        string oldText,
        string newText,
        string description = "Edit Text")
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _editApplier = editApplier ?? throw new ArgumentNullException(nameof(editApplier));
        _range = range;
        _oldText = oldText ?? string.Empty;
        _newText = newText ?? string.Empty;
        Description = description;
    }
    public void Execute()
    {
        if (string.Equals(_oldText, _newText, StringComparison.Ordinal))
        {
            HasChanges = false;
            return;
        }

        var fullSource = _document.SourceMarkdown;
        int start = Math.Clamp(_range.StartOffset, 0, fullSource.Length);
        int length = Math.Clamp(_range.Length, 0, fullSource.Length - start);

        var prefix = fullSource[..start];
        var suffix = fullSource[(start + length)..];
        var updatedSource = prefix + _newText + suffix;

        _editApplier.Apply(_document, updatedSource);
        HasChanges = true;
    }

    public void Undo()
    {
        var fullSource = _document.SourceMarkdown;
        int start = Math.Clamp(_range.StartOffset, 0, fullSource.Length);
        int newTextLen = Math.Clamp(_newText.Length, 0, fullSource.Length - start);

        var prefix = fullSource[..start];
        var suffix = fullSource[(start + newTextLen)..];
        var restoredSource = prefix + _oldText + suffix;

        _editApplier.Apply(_document, restoredSource);
        HasChanges = true;
    }
}