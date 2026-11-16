using System.Collections.Generic;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;

/// <summary>
/// 一个节点最终使用模板的只读快照。由渲染器产出，供检查器等“看结构”的调用方使用：
/// 模板、变体、kind、类名与槽位内容都来自渲染器本身，调用方不再自己推导。
/// </summary>
public sealed record ElementTemplateSnapshot(
    string Variant,
    string? Kind,
    ElementPartSnapshot Root);

/// <summary>
/// 模板里的一个部件。<see cref="Tag"/> 为 null 表示虚拟部件（只输出槽内容）。
/// </summary>
/// <param name="Name">部件 token（root/icon/title/title-text/body/content/wrapper）。</param>
/// <param name="Addressable">该部件是否输出 <c>data-part</c>；内置类型的描述当前一律为 false。</param>
/// <param name="CssClass">已合并 ExtraClass 的最终类名，可能为 null。</param>
/// <param name="InlineCss">仅根部件有值：渲染器写到 style 属性上的完整内联 CSS。</param>
/// <param name="SlotHtml">仅 icon / title-text：槽位填充器产出的成品 HTML。</param>
public sealed record ElementPartSnapshot(
    string Name,
    string? Tag,
    ElementPartRole Role,
    bool Addressable,
    string? CssClass,
    string? InlineCss,
    string? SlotHtml,
    IReadOnlyList<ElementPartSnapshot> Children);
