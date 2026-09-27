using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Threading;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;

internal sealed class DocumentViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly NodeViewModel _rootNode;
    private readonly StyleEditorViewModel _styleEditor;
    private readonly Action<MarkdownNode, NodeStyle> _requestStyleCommit;
    private bool _isDisposed;

    public MarkdownDocument Document { get; }

    public NodeViewModel RootNode => _rootNode;

    internal StyleEditorViewModel StyleEditor => _styleEditor;

    public string FilePath => Document.FilePath;
    public bool IsModified => Document.IsModified;

    public event Action? StyleMutated;
    public event PropertyChangedEventHandler? PropertyChanged;

    public DocumentViewModel(
        MarkdownDocument document,
        IUiThreadDispatcher? uiDispatcher = null,
        IStylePreviewRenderer? stylePreviewRenderer = null,
        Action<MarkdownNode, NodeStyle>? requestStyleCommit = null)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        _requestStyleCommit = requestStyleCommit ?? ApplyStyleDirectly;
        _rootNode = new NodeViewModel(document.Root, OnStyleCommitRequested);
        _styleEditor = new StyleEditorViewModel(uiDispatcher, stylePreviewRenderer);
    }

    private void OnStyleCommitRequested(MarkdownNode node, NodeStyle newStyle)
    {
        if (_isDisposed) return;
        _requestStyleCommit(node, newStyle);
    }

    private void ApplyStyleDirectly(MarkdownNode node, NodeStyle newStyle)
    {
        lock (Document)
        {
            node.LocalStyle = newStyle;
            Document.MarkStyleChanged();
        }

        OnPropertyChanged(nameof(IsModified));
        StyleMutated?.Invoke();
    }

    public void RefreshAst()
    {
        if (_isDisposed) return;

        lock (Document)
        {
            _rootNode.SyncTo(Document.Root);
        }

        OnPropertyChanged(nameof(RootNode));
        OnPropertyChanged(nameof(IsModified));
    }

    public void NotifyNodeStyleChanged(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return;

        var target = FindNodeViewModel(_rootNode, nodeId);
        target?.RaiseStylePropertiesChanged();
    }

    private static NodeViewModel? FindNodeViewModel(NodeViewModel current, string nodeId)
    {
        if (string.Equals(current.Id, nodeId, StringComparison.Ordinal))
            return current;

        foreach (var child in current.Children)
        {
            var match = FindNodeViewModel(child, nodeId);
            if (match is not null) return match;
        }

        return null;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        StyleMutated = null;
        _styleEditor.Dispose();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
