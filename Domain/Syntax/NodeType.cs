namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;


/// <summary>
/// Defines the specific syntactic category of an AST node.
/// </summary>
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

    HtmlBlock,
    HtmlInline,
    Custom
}