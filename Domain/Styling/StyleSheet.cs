using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using System.Collections.ObjectModel;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

public sealed class StyleSheet
{
    public ObservableCollection<StyleDefinition> Styles { get; } = new();

    public StyleDefinition? FindById(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Styles.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));

    public StyleDefinition GetOrCreateDefault()
    {
        var style = FindById("default");
        if (style is not null) return style;

        style = new StyleDefinition
        {
            Id = "default",
            Name = "Default",
            AppliesTo = NodeType.Document,
            Style = new NodeStyle
            {
                FontFamily = "Noto Sans SC",
                FontSize = 14,
                ForegroundColor = "#333333"
            }
        };

        Styles.Add(style);
        return style;
    }
}
