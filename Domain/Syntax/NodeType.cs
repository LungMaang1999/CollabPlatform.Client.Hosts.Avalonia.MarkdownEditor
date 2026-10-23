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
    Subscript,
    Superscript,
    Inserted,
    Marked,
    InlineCode,

    FootnoteLink,
    FootnoteGroup,
    Footnote,
    YamlFrontMatter,

    MathInline,
    MathBlock,

    Mermaid,

    ImageMatrix,

    QrCode,

    PoemBlock,

    Timeline,
    TimelineItem,

    // PoemBlock 的可展开部件（虚拟节点，IsSynthetic=true）。
    // 它们不对应独立源码区间，仅承载各自 LocalStyle 以逐项调整呈现。
    PoemTitle,
    PoemAuthor,
    PoemDynasty,
    PoemForm,
    PoemStanza,
    PoemLine,
    PoemSource,
    PoemNote,

    HtmlBlock,
    HtmlInline,
    Custom
}
