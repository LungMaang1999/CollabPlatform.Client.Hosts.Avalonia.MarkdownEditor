using System.Collections.Generic;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;

/// <summary>
/// 图求值产出的部件结构：只描述分区名、标签、类名与嵌套关系。
/// 内容、样式与节点身份仍由渲染器按槽位填充，不是第二套发射器。
/// </summary>
public sealed class ElementStructureNode
{
    public required string Part { get; init; }

    /// <summary>为 null 表示透明部件：不输出元素，只展开子部件。</summary>
    public string? Tag { get; init; }

    public string? Class { get; init; }

    public ElementPartRole Role { get; init; } = ElementPartRole.Content;

    /// <summary>固定槽内容：给了就替代该分区原本的填充结果。</summary>
    public string? SlotHtml { get; init; }

    /// <summary>写进该部件的内联样式（来自「解样式」阶段）。</summary>
    public string? StyleCss { get; init; }

    /// <summary>
    /// 标签插值原文（如 <c>h{level}</c>）。与结构声明同义：
    /// 渲染期按块已声明字段展开，<see cref="Tag"/> 只放静态标签。
    /// </summary>
    public string? TagTemplate { get; init; }

    /// <summary>原始 class 串（含条件 token）。<see cref="Class"/> 只放无条件 token。</summary>
    public string? RawClass { get; init; }

    public bool Addressable { get; init; } = true;

    /// <summary>部件条件（<c>field==value</c>），渲染期按整块已声明字段求值。</summary>
    public string? When { get; init; }

    /// <summary>源数据条件（固定枚举）：按解析结果决定是否发射本部件及其子树，不命中零发射。</summary>
    public string? WhenSource { get; init; }

    /// <summary>固定集合重复声明；空集合时本部件零项输出。</summary>
    public string? Repeat { get; init; }

    public int RepeatMax { get; init; } = 64;

    public string? Generator { get; init; }

    public string? GeneratorParams { get; init; }

    /// <summary>引用派生值（<c>slug</c>/<c>ordinal</c>/<c>backrefs</c>）。</summary>
    public string? Ref { get; init; }

    /// <summary>
    /// 原文槽：内容取块的源文本并<b>转义</b>写入。空或纯空白时部件仍发射、内容为零。
    /// 与 <see cref="Ref"/> / <see cref="Repeat"/> / <see cref="Generator"/> 互斥（由结构净化保证）。
    /// </summary>
    public bool Raw { get; init; }

    /// <summary>
    /// 槽位文本模板 <c>{field}</c>：取该块已声明属性的值，转义后写入内容。
    /// 与 <see cref="Raw"/> 的区别是数据源（字段值 vs 块源文本），两者都只输出转义文本。
    /// </summary>
    public string? Text { get; init; }

    /// <summary>受控 href 绑定（<c>{field}</c>），只允许 <c>a</c>；值在渲染期按协议白名单校验。</summary>
    public string? Href { get; init; }

    /// <summary>受控 src 绑定（<c>{field}</c>），只允许 <c>img</c>；值在渲染期按协议白名单校验。</summary>
    public string? Src { get; init; }

    public List<ElementStructureNode> Children { get; } = [];
}
