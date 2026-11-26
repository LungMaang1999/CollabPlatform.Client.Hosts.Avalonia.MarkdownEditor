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

    Radar,

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
    Custom,

    // 新增类型一律追加在末尾：枚举数值参与块签名哈希，插队会让既有文档全部换签名。
    Collapse,
    Spoiler,
    Tocpic,
    ColorChip,
    ColorBlock,
    Divider,

    /// <summary>锚点矩阵：由文档标题自动生成的卡片网格，语法 <c>[ANCHORMATRIX depth=2 cols=3]</c>。</summary>
    AnchorMatrix
}
