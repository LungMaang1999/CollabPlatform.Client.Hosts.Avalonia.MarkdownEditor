using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// 一条用户可定义的块级元素语法。属于文档定义（<c>.md.elements.xml</c>），
/// 随文档打开加载，不随单次编辑变化。
/// </summary>
public sealed record UserBlockDefinition
{
    /// <summary>稳定标识，写入节点的 custom-kind，不参与源码匹配。</summary>
    public required string Id { get; init; }

    /// <summary>
    /// 继承的父定义 Id（<c>extends="…"</c>，大小写不敏感），指向同一
    /// <c>.md.elements.xml</c> 里的另一条 <c>&lt;element&gt;</c>。
    /// 只存在于<b>来源形态</b>里：装载/校验边界把父链合并成普通定义后，
    /// 渲染器与解析器看到的定义永远没有该字段；落盘只写来源形态。
    /// </summary>
    public string? Extends { get; init; }

    /// <summary>
    /// 源 XML 里实际出现的元素属性名与子元素名（小写）；<c>null</c> 表示“未知”，
    /// 按全部字段都已声明处理（只用于代码内手工构造的定义）。
    /// 继承合并靠它区分“子写了默认值”与“子压根没写”：未写的字段沿用父值。
    /// </summary>
    public IReadOnlySet<string>? DeclaredFields { get; init; }

    /// <summary>开围栏后的名称，按整词匹配。</summary>
    public required string Name { get; init; }

    /// <summary>
    /// 目标内置类型名（<c>NodeType</c> 枚举名，如 <c>Paragraph</c>）。
    /// 非空表示这条定义不声明新的 <c>:::</c> 元素，而是重写某个内置类型的结构描述；
    /// 它不参与围栏关键字竞争，也不进入解析器规则表。
    /// </summary>
    public string? AppliesTo { get; init; }

    /// <summary>
    /// 仅 <see cref="AppliesTo"/> 定义有效：该内置类型是否走描述通道渲染。
    /// null 表示沿用缺省（true）；<c>Document</c>/<c>Section</c> 这类透明容器必须显式 false。
    /// </summary>
    public bool? RenderEnabled { get; init; }

    /// <summary>仅 <see cref="AppliesTo"/> 定义有效：结构键（当前只允许 <c>poem</c>）。</summary>
    public string? StructureKey { get; init; }

    /// <summary>
    /// 仅 <see cref="AppliesTo"/> 定义有效：模板选择器（<c>admonition:present|absent</c>）。
    /// 按节点已声明属性的存在性选择结构，不做类型判断。
    /// </summary>
    public string? Selector { get; init; }

    /// <summary>
    /// 仅 <see cref="AppliesTo"/> 定义有效：结构变体名（<c>default/card/minimal/left/right/center</c>）。
    /// 一个定义只描述一条结构，同一类型的不同变体各写一条；缺省为 <c>default</c>。
    /// </summary>
    public string? Variant { get; init; }

    /// <summary>
    /// 仅 <see cref="AppliesTo"/> 定义有效：容器内条目行的起始标记（如 <c>@@</c>）。
    /// 缺省为空表示沿用 <c>@@</c>；标记之后的条目语法（时间/标签/侧别）不随标记变化。
    /// </summary>
    public string? ItemMarker { get; init; }

    /// <summary>
    /// 首词分派声明：本定义的关键字（<see cref="Name"/> 与 <see cref="Aliases"/>）
    /// 在 <see cref="EffectiveFenceChars"/> 指定的围栏族下开启哪一个块类型。
    /// 取值限定现有 <c>NodeType</c> 白名单；<c>Custom</c> 表示仍走自定义元素通道（自定义定义的缺省值）。
    /// 为空表示该定义不贡献语法形态，内置关键字表原样生效。
    /// </summary>
    public string? Opens { get; init; }

    /// <summary>一个定义最多允许声明的别名数量。</summary>
    public const int MaxAliases = 8;

    /// <summary>
    /// 该定义的额外关键字（别名）。别名与本名指向同一元素，解析、渲染、
    /// 节点 <c>custom-kind</c> 完全一致；为空表示没有别名，历史定义行为不变。
    /// </summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>该定义接受的完整关键字集合：本名在首位，其后按声明顺序排列别名。</summary>
    public IReadOnlyList<string> EffectiveKeywords => Aliases.Count > 0 ? [Name, .. Aliases] : [Name];

    /// <summary>指定关键字是否命中本定义（整词、忽略大小写）。解析热路径用，不分配集合。</summary>
    public bool MatchesKeyword(string? keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return false;
        if (string.Equals(Name, keyword, StringComparison.OrdinalIgnoreCase)) return true;

        for (var index = 0; index < Aliases.Count; index++)
        {
            if (string.Equals(Aliases[index], keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// 旧版单围栏字符（默认 <c>:</c>）。单独声明时等价于只含一个字符的
    /// <see cref="FenceChars"/>；两者同时给出时首项必须与它一致。
    /// </summary>
    public char FenceChar { get; init; } = ':';

    /// <summary>
    /// 多围栏字符声明（例如同时支持 <c>```kind</c> 与 <c>~~~kind</c>）。
    /// 为空表示沿用旧的 <see cref="FenceChar"/>；非空时首项必须等于 <see cref="FenceChar"/>。
    /// </summary>
    public IReadOnlyList<char> FenceChars { get; init; } = [];

    /// <summary>该定义实际接受的围栏字符集合（多字符优先，否则回落单字符）。</summary>
    public IReadOnlyList<char> EffectiveFenceChars => FenceChars.Count > 0 ? FenceChars : [FenceChar];

    /// <summary>该定义是否接受指定围栏字符；解析热路径用，避免分配集合。</summary>
    public bool UsesFenceChar(char fenceChar) =>
        FenceChars.Count > 0 ? FenceChars.Contains(fenceChar) : FenceChar == fenceChar;

    /// <summary>开围栏最少字符数。</summary>
    public int FenceLength { get; init; } = 3;

    /// <summary>同名定义的优先级，数值大者胜。</summary>
    public int Priority { get; init; }

    /// <summary>允许的实例属性声明；不影响块的语法边界。</summary>
    public IReadOnlyList<UserBlockAttributeDeclaration> Attributes { get; init; } = [];

    /// <summary>
    /// 引用的属性组 Id（<c>&lt;uses ref="…"/&gt;</c>，按声明顺序）。
    /// 装载期展开时这些组的属性按声明顺序追加到 <see cref="Attributes"/> 之后；
    /// 保存时按本字段写回 ref 原文，不写展开结果，保证 <c>save → load → save</c> 不丢结构。
    /// </summary>
    public IReadOnlyList<string> AttributeGroups { get; init; } = [];

    /// <summary>
    /// 该定义直接与传递引用到的组合单元 Id 集合（属性组 + 结构片段，去重、声明顺序）。
    /// 进语法指纹与运行期缓存键：改被引用的片段必须让引用者失效。
    /// 展开时由 <see cref="UserBlockCompositionRules"/> 填写，序列化读入时也会填。
    /// </summary>
    public IReadOnlyList<string> CompositionRefs { get; init; } = [];

    /// <summary>内容模型：允许出现在块内的块级/行内元素。</summary>
    public UserBlockContentModel Content { get; init; } = UserBlockContentModel.Empty;

    /// <summary>
    /// 渲染结构声明：这个元素由哪些标签、哪些槽位构成。
    /// 为空表示未声明，渲染回退到该 <c>(类型, 变体)</c> 的内置模板，输出逐字节不变。
    /// </summary>
    public UserBlockStructure Structure { get; init; } = UserBlockStructure.Empty;

    /// <summary>
    /// 结构图：为空表示没有挂图，渲染按 <see cref="Structure"/> 或内置模板。
    /// 有图时由「取结构」的实际产出决定结构声明，<see cref="Structure"/> 退为图产出为空时的回落。
    /// 图本身不写进 <c>.md.elements.xml</c>，随 <c>.md.graph.xml</c> 加载时挂上。
    /// </summary>
    public NodeGraph? StructureGraph { get; init; }

    /// <summary>
    /// 仅 <c>applies-to="HtmlInline"</c> 定义有效：行内 HTML 的受控白名单。
    /// 为空表示该类型不保留任何标签（解析产物与翻案前一致）。
    /// 声明只决定“可以放行什么”，机制层的禁止项（<c>on*</c>/<c>class</c>/
    /// <c>data-*</c>/<c>id</c> 与危险协议）永远不可放宽。
    /// </summary>
    public InlineHtmlDefinition? InlineHtml { get; init; }

    /// <summary>
    /// 仅 <c>applies-to="HtmlBlock"</c> 定义有效：块级 HTML 的受控白名单。
    /// 为空表示该类型不保留任何标签、整段仍按文本转义（与翻案前逐字节一致）。
    /// 块内一律按本节判定，<b>不与 <see cref="InlineHtml"/> 取并集</b>：
    /// 想在块 HTML 里保留某个标签，必须在本节也声明。
    /// </summary>
    public InlineHtmlDefinition? BlockHtml { get; init; }
}
