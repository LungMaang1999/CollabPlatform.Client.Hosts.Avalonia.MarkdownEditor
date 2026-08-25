using System.ComponentModel;
using System.Runtime.CompilerServices;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;

public sealed class DocumentViewModel : INotifyPropertyChanged
{
    private NodeViewModel _rootNode;
    private readonly StyleEditorViewModel _styleEditor;

    public MarkdownDocument Document { get; }

    public NodeViewModel RootNode
    {
        get => _rootNode;
        private set => SetField(ref _rootNode, value);
    }

    public StyleEditorViewModel StyleEditor => _styleEditor;
    public string FilePath => Document.FilePath;
    public bool IsModified => Document.IsModified;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DocumentViewModel(MarkdownDocument document)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        _rootNode = new NodeViewModel(document.Root);
        _styleEditor = new StyleEditorViewModel(document);
    }

    /// <summary>
    /// 当文本解析产生新的 AST 时，刷新大纲树
    /// </summary>
    public void RefreshAst()
    {
        RootNode = new NodeViewModel(Document.Root);
        OnPropertyChanged(nameof(IsModified));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}