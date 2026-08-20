using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using System.Collections.ObjectModel;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;

public sealed class StyleEditorViewModel
{
    private readonly MarkdownDocument _document;
    public ObservableCollection<StyleDefinition> Styles => _document.StyleSheet.Styles;

    public StyleEditorViewModel(MarkdownDocument document) =>
        _document = document ?? throw new ArgumentNullException(nameof(document));
}