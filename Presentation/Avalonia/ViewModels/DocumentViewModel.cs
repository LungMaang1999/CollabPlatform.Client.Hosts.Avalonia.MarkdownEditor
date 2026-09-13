using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Threading;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;

/// <summary>
/// 表示当前激活文档的 ViewModel（内部密封类），管理文档状态与节点树。
/// </summary>
internal sealed class DocumentViewModel : INotifyPropertyChanged, IDisposable
{
    private NodeViewModel _rootNode;
    private readonly StyleEditorViewModel _styleEditor;
    private bool _isDisposed;

    public MarkdownDocument Document { get; }

    public NodeViewModel RootNode
    {
        get => _rootNode;
        private set => SetField(ref _rootNode, value);
    }

    internal StyleEditorViewModel StyleEditor => _styleEditor;

    public string FilePath => Document.FilePath;
    public bool IsModified => Document.IsModified;

    public event Action? StyleMutated;
    public event PropertyChangedEventHandler? PropertyChanged;

    public DocumentViewModel(MarkdownDocument document, IUiThreadDispatcher? uiDispatcher = null)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        _rootNode = new NodeViewModel(document.Root, OnNodeStyleMutated);
        _styleEditor = new StyleEditorViewModel(uiDispatcher: uiDispatcher);
    }

    private void OnNodeStyleMutated()
    {
        if (_isDisposed) return;
        Document.MarkStyleChanged();
        OnPropertyChanged(nameof(IsModified));
        StyleMutated?.Invoke();
    }

    public void RefreshAst()
    {
        if (_isDisposed) return;

        lock (Document)
        {
            if (_rootNode is not null && _rootNode.Node == Document.Root)
            {
                _rootNode.UpdateInternalNode(Document.Root);
            }
            else
            {
                RootNode = new NodeViewModel(Document.Root, OnNodeStyleMutated);
            }
        }

        OnPropertyChanged(nameof(RootNode));
        OnPropertyChanged(nameof(IsModified));
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

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}