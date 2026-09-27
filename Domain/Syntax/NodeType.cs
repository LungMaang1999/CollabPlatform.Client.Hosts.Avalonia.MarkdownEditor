namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

public enum NodeType
{
    Document,
    Section,

    Heading,
    Paragraph,
    Text,
    LineBreak,

    UnorderedList,
    OrderedList,
    ListItem,
    TaskListItem,

    Quote,
    CodeBlock,
    ThematicBreak,

    Table,
    TableRow,
    TableCell,

    Link,
    Image,
    Strong,
    Emphasis,
    Delete,
    InlineCode,

    FootnoteLink,
    FootnoteGroup,
    Footnote,
    YamlFrontMatter,

    MathInline,
    MathBlock,

    Mermaid,

    HtmlBlock,
    HtmlInline,
    Custom
}
