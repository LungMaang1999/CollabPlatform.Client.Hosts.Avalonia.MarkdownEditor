using System;
using System.Collections.Generic;
using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;

/// <summary>模板渲染上下文：一个部件填充器只负责往当前元素内部写内容。</summary>
internal sealed class TemplateRenderContext
{
    /// <summary>模板实例的根节点；整块渲染期间不变。</summary>
    public required MarkdownNode Node { get; init; }

    public required StringBuilder Html { get; init; }
    public required List<DiagnosticMessage> Diagnostics { get; init; }

    /// <summary>
    /// 当前部件所代表的节点：节点集合重复的当前项，或来源子节点条件命中的来源节点。
    /// 无覆盖时等于 <see cref="Node"/>；样式、文本与动态属性都按它取值。
    /// </summary>
    public required MarkdownNode CurrentNode { get; set; }

    /// <summary>repeat 展开时的当前项（原始 token），非 token 集合为 null。</summary>
    public string? RepeatItem { get; set; }

    /// <summary>repeat 展开时当前项的 1 基序号（跳过空项），非 repeat 内为 null。</summary>
    public int? RepeatOrdinal { get; set; }

    /// <summary>repeat 嵌套深度：0 表示不在 repeat 内，诗体只允许两层（最大深度 2）。</summary>
    public int RepeatDepth { get; set; }

    /// <summary>当前所在 repeat 的集合名；用于校验“外层 stanzas、内层 lines”这一固定配对。</summary>
    public string? RepeatCollection { get; set; }
}

/// <summary>部件填充器：不负责开闭标签，只写部件内部内容。</summary>
internal delegate void PartWriter(TemplateRenderContext context);

/// <summary>生成器输出阶段：先补生成属性，再写内容。</summary>
internal enum PartGeneratorPhase
{
    /// <summary>在开标签的 <c>&gt;</c> 之前追加生成属性。</summary>
    Attributes,

    /// <summary>在开标签之后写生成内容。</summary>
    Content,
}

/// <summary>
/// 生成器上下文。算法只产出「内容 + 受控生成属性」：
/// 标签、class、节点身份、内联样式与类型级绑定仍由模板发射器统一处理，
/// 生成器不得自行开闭根元素，也不得按类型名分派。
/// </summary>
internal sealed class PartGeneratorContext
{
    public required MarkdownNode Node { get; init; }

    /// <summary>
    /// 生成器读取的源文本：内置围栏类节点取 <see cref="MarkdownNode.Text"/>；
    /// 容器节点（如自定义元素）取块体内容。算法只消费这段文本，不感知节点类型。
    /// </summary>
    public required string Source { get; init; }

    public required StringBuilder Html { get; init; }

    public required List<DiagnosticMessage> Diagnostics { get; init; }

    /// <summary>按 key 排序后的封闭参数（来自描述的 <c>generator-params</c>）。</summary>
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }

    public required PartGeneratorPhase Phase { get; init; }

    /// <summary>属性阶段的可写通道；名字由生成器代码固定，值统一转义。</summary>
    public Action<string, string>? AttributeSink { get; init; }

    public void AppendAttribute(string name, string value) => AttributeSink?.Invoke(name, value);

    public string? Parameter(string name) =>
        Parameters.TryGetValue(name, out var value) ? value : null;
}

/// <summary>命名生成器：注册表按键取算法，渲染路径不感知具体节点类型。</summary>
internal delegate void PartGenerator(PartGeneratorContext context);

/// <summary>
/// 来源子节点条件：只允许“某类来源子节点存在/缺失”这一种判断。
/// 固定枚举、无表达式、无组合运算；只决定是否发射该部件及其子树，不改结构形态。
/// 组谓词（Header / AuthorLine / SourceAndNote）也是不可再组合的固定原子，
/// 唯一用途是保持诗体容器的既有输出（空容器不发射、断行只在两段共存时出现）。
/// </summary>
internal enum PartSourceCondition
{
    /// <summary>无来源条件，始终发射。</summary>
    Always,

    PoemTitle,
    PoemAuthor,
    PoemDynasty,
    PoemForm,
    PoemSource,
    PoemNote,

    /// <summary>标题 / 作者 / 朝代 / 体裁任一存在。</summary>
    PoemHeader,

    /// <summary>作者 / 朝代任一存在。</summary>
    PoemAuthorLine,

    /// <summary>出处与说明同时存在（断行 br 的唯一触发条件）。</summary>
    PoemSourceAndNote,
}

/// <summary>
/// 模板结构键：只有显式声明结构键的模板才放开对应的结构约束。
/// 当前只有诗体放开“stanzas → lines”两级重复；未声明的模板仍拒绝任何嵌套重复。
/// </summary>
internal enum ElementStructureKey
{
    None,

    /// <summary>诗体：允许固定两级重复，受外层 ≤32、内层 ≤64、总展开 ≤1024 约束。</summary>
    Poem,
}

/// <summary>
/// 动态属性值来源。固定枚举，不允许表达式、拼接或格式化模板；
/// 新增来源必须同时补白名单校验与渲染求值，不接受字符串描述。
/// </summary>
internal enum PartAttributeValueSource
{
    /// <summary>定义里的常量值。</summary>
    Constant,

    /// <summary>解析器写在节点上的已声明属性字段。</summary>
    NodeAttribute,

    /// <summary>节点文本（如 <c>alt</c>）。</summary>
    NodeText,

    /// <summary>repeat 当前项原文。</summary>
    RefBackrefItem,

    /// <summary>repeat 当前项对应的页内锚点（<c>#</c> + 项）。</summary>
    RefBackrefHref,

    /// <summary>repeat 当前项的「返回第 N 处引用」文案。</summary>
    RefBackrefTitle,

    /// <summary>repeat 当前项的「返回第 N 处正文引用」文案。</summary>
    RefBackrefAriaLabel,

    /// <summary>解析器给的脚注目标 id（<c>fn-</c> + slug）。</summary>
    RefTarget,

    /// <summary>
    /// 用户元素定义里的受控 URL 绑定：值来自该块<b>已声明字段</b>，
    /// 只用于 <c>a</c>→<c>href</c> / <c>img</c>→<c>src</c>，并走解码后的协议白名单。
    /// </summary>
    DeclaredFieldUrl,
}

/// <summary>
/// 标签发射条件。只决定“是否发射外层标签”，不承载通用条件渲染：
/// 条件不成立时只写部件内容与子部件，绝不按字段选择结构。
/// </summary>
internal enum PartTagCondition
{
    /// <summary>始终发射标签。</summary>
    Always,

    /// <summary>节点解析出非空内联样式、且未旁路样式时才发射（Text 的 span）。</summary>
    HasInlineStyle,
}

/// <summary>
/// P7 发射策略：只决定“这个元素是否产出 HTML”，不决定标签、属性、内容或顺序。
/// <see cref="None"/> 只能由内置描述声明（定义文件无法设置），服务隐藏元素（Front Matter）：
/// “空外形”由此声明给出，而不是渲染器里的类型判断。与既有标签冲突时整块拒绝，不静默丢弃。
/// </summary>
internal enum PartEmitMode
{
    /// <summary>正常发射。</summary>
    Emit,

    /// <summary>显式不发射：本部件及其子树不产出任何字节。</summary>
    None,
}

/// <summary>
/// 属性驱动的结构变体选择键：固定枚举，只允许已声明的 Enum 属性。
/// 新增键属于机制变更，必须单独立项，不得在分支里散写属性名。
/// </summary>
internal enum TemplateSelectorKey
{
    /// <summary>节点是否带 <c>admonition</c> 属性：有 → 提示块结构，无 → 普通引用结构。</summary>
    Admonition,
}

/// <summary>选择器只区分属性“存在/缺失”两种匹配，不支持取值、表达式、嵌套或组合键。</summary>
internal enum TemplateSelectorMatch
{
    Absent,
    Present,
}

/// <summary>
/// 部件上的一个动态属性。名字必须命中固定白名单；<see cref="Policy"/> 只允许
/// <c>link</c> / <c>backlink</c> / <c>image</c>，由渲染器按既有 URL 策略判定。
/// </summary>
internal sealed record PartAttributeTemplate(
    string Name,
    PartAttributeValueSource Source,
    string? Value = null,
    string? Policy = null);

/// <summary>
/// 动态属性白名单与固定诊断码。只接受逐字列出的属性名，
/// 不做前缀/通配匹配（<c>on*</c>、任意 <c>data-*</c>、<c>src</c>、<c>style</c> 一律拒绝）。
/// </summary>
internal static class PartAttributeRules
{
    public const string ForbiddenNameCode = "ELEMENT_ATTRIBUTE_NAME_FORBIDDEN";
    public const string UnsafeUrlCode = "ELEMENT_ATTRIBUTE_URL_UNSAFE";

    public const string ForbiddenSourceCode = "ELEMENT_ATTRIBUTE_SOURCE_FORBIDDEN";

    private static readonly HashSet<string> AllowedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "href",
        "title",
        "aria-label",
        "type",
        "disabled",
        "class",
        // 脚注回链的既有公开契约属性：固定字面名，不是 data-* 通配。
        "data-footnote-target",
        // 图片受控通道：名字固定，但取值来源被逐项钉死（见 IsAllowedSource）。
        "src",
        "loading",
        "alt",
        // 时间线条目：取值只能来自解析器派生字段 timeline-side-effective，值域 left/right。
        "data-side",
        // 时间线轨道：固定常量 true，纯无障碍标记。
        "aria-hidden",
    };

    public static bool IsAllowedName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && AllowedNames.Contains(name);

    public static bool IsUrlAttribute(string name) =>
        string.Equals(name, "href", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "src", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 固定名对应的取值来源约束。只接受枚举来源，不接受任意字段名、表达式或常量：
    /// <c>src</c> 只能取解析器图像 URL 字段并走 image 策略；<c>loading</c> 只能是固定常量
    /// <c>lazy</c>；<c>alt</c> 只能取节点文本。
    /// </summary>
    public static bool IsAllowedSource(PartAttributeTemplate attribute) =>
        attribute.Name.ToLowerInvariant() switch
        {
            "src" => (attribute.Source == PartAttributeValueSource.NodeAttribute
                      && string.Equals(attribute.Value, "url", StringComparison.Ordinal)
                      && string.Equals(attribute.Policy, "image", StringComparison.Ordinal))
                     // 用户元素定义里的受控图片绑定：字段名由定义声明，值仍走同一套 URL 白名单。
                     || (attribute.Source == PartAttributeValueSource.DeclaredFieldUrl
                         && !string.IsNullOrWhiteSpace(attribute.Value)),
            "loading" => attribute.Source == PartAttributeValueSource.Constant
                         && string.Equals(attribute.Value, "lazy", StringComparison.Ordinal),
            "alt" => attribute.Source == PartAttributeValueSource.NodeText,
            // 只接受解析器派生字段；不接受常量字面量，值域另由 IsAllowedValue 钉死。
            "data-side" => attribute.Source == PartAttributeValueSource.NodeAttribute
                           && string.Equals(attribute.Value, "timeline-side-effective", StringComparison.Ordinal),
            "aria-hidden" => attribute.Source == PartAttributeValueSource.Constant
                             && string.Equals(attribute.Value, "true", StringComparison.Ordinal),
            _ => true,
        };

    /// <summary>固定名对应的取值域约束；未列出的名字不做额外限制。</summary>
    public static bool IsAllowedValue(PartAttributeTemplate attribute, string? value) =>
        attribute.Name.ToLowerInvariant() switch
        {
            "data-side" => string.Equals(value, "left", StringComparison.Ordinal)
                           || string.Equals(value, "right", StringComparison.Ordinal),
            _ => true,
        };
}

/// <summary>
/// 结构模板中的一个部件。模板只描述“标签 / class / 顺序 / 包裹”，
/// 内容由 <see cref="ElementTemplate.Slots"/> 里的填充器产出。
/// </summary>
internal sealed class PartTemplate
{
    public required string Part { get; init; }

    /// <summary>为 null 时表示虚拟部件：不输出元素，只执行填充器与子部件。</summary>
    public string? Tag { get; init; }

    public string? Class { get; init; }

    /// <summary>
    /// 是否输出 <c>data-part</c>。内置类型当前一律不输出（其输出由直出代码决定），
    /// 因此描述通道登记这些部件时全部为 false；改动会让输出多出属性、破坏字节基线。
    /// 已登记模板（Quote/Custom/Timeline）保持 true。
    /// </summary>
    public bool Addressable { get; init; } = true;

    /// <summary>
    /// 部件自己的盒子布局声明（已规范化）。为空时不输出 style，历史定义输出不变。
    /// 与 bindingCss 合并时排在其前，保证「元素定义是基底、实例样式可覆盖」。
    /// </summary>
    public string? Style { get; init; }

    public ElementPartRole Role { get; init; }

    /// <summary>
    /// 该部件自己的固定内容。非空时独占本部件内容，不再按分区名取共享槽填充器——
    /// 循环产出的多个同名部件各自携带内容，共享槽会互相覆盖。
    /// </summary>
    public PartWriter? Content { get; set; }

    /// <summary>
    /// 根部件是否输出节点身份属性（<c>data-node-id</c> / 行区间 / 未闭合标记）与实例样式。
    /// 现状输出里不携带节点身份的根（如 <c>br</c>、脚注组 <c>section</c>）置 false，
    /// 输出才不会多出属性。
    /// </summary>
    public bool EmitNodeIdentity { get; set; } = true;

    /// <summary>
    /// 空元素（<c>br</c> / <c>hr</c>）：以 <c> /&gt;</c> 收尾，不执行内容、子部件与闭合标签。
    /// </summary>
    public bool SelfClosing { get; set; }

    /// <summary>
    /// P7 发射策略：<see cref="PartEmitMode.None"/> 表示本部件及其子树显式不产出 HTML。
    /// 若同时声明了 <see cref="Tag"/>，属于自相矛盾的结构，整块拒绝并报
    /// <c>ELEMENT_EMIT_CONFLICT</c>，不做静默丢弃。
    /// </summary>
    public PartEmitMode Emit { get; init; } = PartEmitMode.Emit;

    /// <summary>
    /// 标签发射条件。非 <see cref="PartTagCondition.Always"/> 时，条件不成立只写内容，
    /// 不发射标签、属性、节点身份与内联样式。
    /// </summary>
    public PartTagCondition TagCondition { get; set; } = PartTagCondition.Always;

    /// <summary>
    /// 根部件仍输出节点身份，但不输出实例样式（表格滚动容器把样式留给内部 table）。
    /// </summary>
    public bool SuppressNodeStyle { get; set; }

    /// <summary>动态追加的 class（如提示块的类型名）。</summary>
    public Func<MarkdownNode, string?>? ExtraClass { get; init; }

    /// <summary>
    /// 标签插值求值器：定义里写 <c>h{level}</c> 这类受控模板时由此求出实际标签。
    /// 返回 null 表示按虚拟部件处理（只写内容，不发射标签），绝不把 <c>{field}</c> 写进标签位。
    /// 求值只读节点已声明的字段，不做类型判断。
    /// </summary>
    public Func<MarkdownNode, string?>? TagTemplate { get; init; }

    /// <summary>
    /// 部件条件（定义里的 <c>when="field==value"</c>）：返回 false 时本部件及其子树不发射。
    /// 只支持“已声明枚举字段 + 枚举值”的相等判断，由定义内容驱动，渲染器不感知类型名。
    /// </summary>
    public Func<MarkdownNode, bool>? EmitCondition { get; init; }

    /// <summary>
    /// 源数据条件（定义里的 <c>when-source="…"</c>）：固定枚举，按解析结果（正文/子节点/图片）
    /// 决定是否发射本部件及其子树；不命中时零发射，不保留空壳。null 表示不判定。
    /// </summary>
    public string? WhenSource { get; init; }

    /// <summary>
    /// 条件类名求值器：返回完整 class 串（无条件 token 与命中条件的 token 按声明顺序）。
    /// 非空时优先于 <see cref="Class"/>，避免把 <c>name?field==value</c> 写进 class 字节。
    /// </summary>
    public Func<MarkdownNode, string?>? ConditionalClass { get; init; }

    /// <summary>根部件追加的语义 CSS（如强调色变量）。</summary>
    public Func<MarkdownNode, string?>? ExtraCss { get; set; }

    /// <summary>本部件的动态属性（按声明顺序输出，名字受固定白名单约束）。</summary>
    public List<PartAttributeTemplate> Attributes { get; } = [];

    /// <summary>
    /// 属性写在节点身份（根）或内联样式（非根）之后。
    /// 链接/图片的直出顺序是「节点属性 → style → href/src」，其余部件是「class → 属性 → 节点属性」。
    /// </summary>
    public bool AttributesAfterIdentity { get; set; }

    /// <summary>
    /// 非根部件也输出节点自身的内联样式（脚注引用锚点直出即如此）。
    /// </summary>
    public bool EmitNodeStyle { get; set; }

    /// <summary>
    /// 按固定派生集合展开本部件（及子树）：<c>backrefs</c> / <c>timeline-tags</c> /
    /// <c>stanzas</c> / <c>lines</c>。空表示不展开。
    /// 嵌套 repeat 只在声明了 <see cref="ElementTemplate.StructureKey"/> 的模板内放行。
    /// </summary>
    public string? RepeatCollection { get; set; }

    /// <summary>
    /// repeat 展开上限（1..64）。超出上限直接拒绝该部件并给固定诊断码，
    /// 不做截断——不允许把超出的项悄悄丢掉。
    /// </summary>
    public int RepeatMax { get; set; } = 64;

    /// <summary>
    /// 来源子节点条件：只决定是否发射本部件及其子树，不改结构形态。
    /// 命中单个来源子节点时，本部件按该来源节点取文本与样式。
    /// </summary>
    public PartSourceCondition SourceCondition { get; set; } = PartSourceCondition.Always;

    /// <summary>
    /// 固定枚举的成员收集声明：非空时本部件（虚拟容器）只渲染该集合定义的子节点，
    /// 替代渲染器里的 <c>child.Type != X</c> 筛选。
    /// </summary>
    public string? ChildCollection { get; init; }

    /// <summary>成员集合为空时写入的固定内容（占位提示）；只在声明了 <see cref="ChildCollection"/> 时生效。</summary>
    public string? EmptyCollectionHtml { get; init; }

    /// <summary>
    /// 集合存在条件：命名的固定集合非空才发射本部件及其子树（用于“无标签不发射标签容器”）。
    /// 集合名与 <see cref="RepeatCollection"/> 共用同一固定枚举。
    /// </summary>
    public string? EmitWhenCollection { get; set; }

    /// <summary>
    /// 生成器名（固定注册表）。非空时由注册表按名字取算法产出本部件的内容与生成属性；
    /// 为空时走内容槽。名字合法性由 <see cref="UserBlockStructureRules.TryNormalizeGenerator"/> 校验。
    /// </summary>
    public string? Generator { get; set; }

    /// <summary>封闭参数串（<c>k=v;k=v</c>）；只有声明了 <see cref="Generator"/> 才有效。</summary>
    public string? GeneratorParams { get; set; }

    public List<PartTemplate> Children { get; } = [];
}

/// <summary>
/// 一个 <c>(NodeType, Variant)</c> 对应的结构模板。注册表从第一天起就按
/// 二元组建键，避免后续从单变体回改。
/// </summary>
internal sealed class ElementTemplate
{
    public required NodeType NodeType { get; init; }

    public required ElementTemplateVariant Variant { get; init; }

    public required PartTemplate Root { get; init; }

    /// <summary>
    /// 是否参与渲染。描述通道：未迁移类型登记描述但置 false，
    /// <c>TryRenderTemplate</c> 必须跳过，输出仍由原直出代码决定。
    /// 某类型改为模板驱动（字节对照通过后）才翻成 true。
    /// </summary>
    public bool RenderEnabled { get; init; } = true;

    /// <summary>
    /// 结构键：显式声明后才放开对应的结构约束。目前只有 <see cref="ElementStructureKey.Poem"/>
    /// 放开“stanzas → lines”两级重复；未声明的模板仍拒绝任何嵌套重复。
    /// 只能由内置描述声明，用户定义文件无法设置。
    /// </summary>
    public ElementStructureKey StructureKey { get; init; } = ElementStructureKey.None;

    public Dictionary<string, PartWriter> Slots { get; } = new(StringComparer.Ordinal);
}
