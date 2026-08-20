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

    UnorderedList,
    OrderedList,
    ListItem,

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

    HtmlBlock,
    HtmlInline,
    Custom
}