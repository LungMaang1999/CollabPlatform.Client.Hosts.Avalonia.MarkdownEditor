namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>诗体元数据字段种类（解析产物，渲染层再映射为节点类型）。</summary>
public enum PoemFieldKind
{
    Title,
    Author,
    Dynasty,
    Form,
    Source,
    Note
}

/// <summary>诗体元数据字段切片。</summary>
public sealed record PoemFieldSlice(PoemFieldKind Kind, string Text, SourceRange Range);

/// <summary>诗句切片（行号与区间由 parser 侧给出）。</summary>
public sealed record PoemLineSlice(string Text, SourceRange Range);

/// <summary>诗句组切片（空行分段产生）。</summary>
public sealed record PoemStanzaSlice(IReadOnlyList<PoemLineSlice> Lines, SourceRange Range);

/// <summary>
/// 诗体围栏的解析产物：元数据字段 + 诗句组 + 尾部字段。
/// 由 <c>PoemBlock</c> 在首次读取时按源码切分，转换器只按顺序建节点，不再做行解析。
/// </summary>
public sealed record PoemLayout(
    IReadOnlyList<PoemFieldSlice> Header,
    IReadOnlyList<PoemStanzaSlice> Stanzas,
    IReadOnlyList<PoemFieldSlice> Footer,
    SourceRange PartRange);
