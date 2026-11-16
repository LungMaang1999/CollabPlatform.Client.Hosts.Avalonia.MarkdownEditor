using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// 用户元素的结构声明：由“部件 + 槽位”组成的声明树，描述这个元素由哪些标签、
/// 哪些槽位构成、槽位里放什么。与渲染侧的 <c>PartTemplate</c> / <c>ElementTemplate</c>
/// 一一对应，渲染时直接翻译成同一套模板，不另开渲染路径。
///
/// 空结构（<see cref="Empty"/>）表示“未声明结构”，渲染回退到内置模板，输出逐字节不变。
/// </summary>
public sealed record UserBlockStructure
{
    /// <summary>未声明结构的单例：所有历史定义都取它，保证回退路径一致。</summary>
    public static UserBlockStructure Empty { get; } = new();

    /// <summary>结构根部件；为 null 表示没有结构声明。</summary>
    public UserBlockPart? Root { get; init; }

    /// <summary>
    /// 定义级自定义槽位声明（按声明顺序）。内建槽位（root/icon/title/title-text/body/content/wrapper，
    /// 以及 footer/badge/meta）不需要在这里登记；未登记的自定义槽名出现在部件上会被拒绝。
    /// 默认空表，历史定义因此不写 <c>slots</c> 属性、字节保持不变。
    /// </summary>
    public IReadOnlyList<string> Slots { get; init; } = [];

    /// <summary>
    /// 允许无标签部件（透明部件）。只有重写内置类型的定义置 true：
    /// <c>Document</c>/<c>Section</c>、<c>YamlFrontMatter</c>、无标签的脚注 repeat 部件
    /// 在直出代码里就是“只走内容、不发标签”，数据面必须能表达同一种形态。
    /// 用户自定义元素仍要求标签，历史定义的拒绝行为不变。
    /// </summary>
    public bool AllowTransparentParts { get; init; }

    public bool IsEmpty => Root is null;
}

/// <summary>
/// 结构中的一个部件：一个标签 + 可选类名 + 可选槽位 + 子部件。
/// <see cref="Part"/> 是槽位 token（root/icon/title/title-text/body/content），
/// 纯包裹层的 <see cref="Part"/> 为 <see cref="UserBlockStructureRules.WrapperPart"/>，不承载内容。
/// </summary>
public sealed record UserBlockPart
{
    /// <summary>槽位 token 或包裹层 token，取值必须来自 <see cref="UserBlockStructureRules"/>。</summary>
    public required string Part { get; init; }

    /// <summary>
    /// 结构片段引用：<c>&lt;include ref="…"/&gt;</c> 的片段 Id。非 null 时本节点是占位符，
    /// 装载期被展开为片段根部件；展开后的结构里不再出现该字段，
    /// 因此渲染器与解析器永远看不到引用。此时 <see cref="Part"/> 取
    /// <see cref="UserBlockStructureRules.IncludePartToken"/>，不代表真实槽位。
    /// </summary>
    public string? Include { get; init; }

    /// <summary>
    /// 白名单内的标签名（小写），或 schema v2 的受控插值 <c>{field}</c> /
    /// <c>前缀{field}</c>（如 <c>h{level}</c>）。当前只允许一个洞；
    /// 前缀限安全字符且不超长，展开后的完整标签仍须落在标签白名单内。
    /// </summary>
    /// <summary>
    /// 输出标签；<c>null</c> 表示透明部件（只走内容/子部件，不发标签）。
    /// 只有内置重写定义允许透明部件，自定义元素仍必须给出标签。
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>
    /// 是否输出 <c>data-part</c>。schema v1 缺省为 true，历史结构因此逐字节不变；
    /// 仅当定义显式写 <c>addressable="false"</c> 时才为 false（如 Heading 的 <c>h{level}</c>）。</summary>
    public bool Addressable { get; init; } = true;

    /// <summary>
    /// schema v2 条件：<c>{field}=={value}</c>（已规范化）。null 表示无条件。
    /// 条件只允许“已声明枚举字段 + 枚举值”，不含表达式、取反或脚本；
    /// 为 false 时整个部件（含子部件）不发射。
    /// </summary>
    public string? When { get; init; }

    /// <summary>
    /// schema v2 源数据条件：按<b>解析结果</b>（正文是否为空、是否有块级子节点、是否有图片）
    /// 决定是否发射本部件及其子树。取值是固定枚举（<see cref="UserBlockStructureRules.WhenSources"/>），
    /// 不接受字段名、表达式或组合；不命中时本部件及子树<b>零发射</b>（不保留空壳）。
    /// 与 <see cref="When"/> 互斥：同一个部件只能有一个条件槽位。
    /// </summary>
    public string? WhenSource { get; init; }

    /// <summary>
    /// schema v2 重复：固定派生集合名（<c>lines</c>/<c>stanzas</c>/<c>images</c>/<c>backrefs</c>/
    /// <c>timeline-tags</c>/<c>children</c>/<c>body-lines</c>），null 表示不重复。
    /// 集合名不是属性字段名，也不是表达式——重复源只能来自解析器派生数据，
    /// 属性声明里没有集合类型，因此这里不接受任意字段。
    /// </summary>
    public string? Repeat { get; init; }

    /// <summary>
    /// 重复上限（1..<see cref="UserBlockStructureRules.DefaultRepeatMax"/>）；
    /// 无重复时恒为缺省值，不参与字节与指纹。
    /// </summary>
    public int RepeatMax { get; init; } = UserBlockStructureRules.DefaultRepeatMax;

    /// <summary>
    /// schema v2 生成器：固定注册表内的命名算法（<c>math</c>/<c>mermaid</c>/<c>image-matrix</c>/
    /// <c>qrcode</c>/<c>radar</c>/<c>code-highlight</c>），null 表示不生成。
    /// 定义只声明“用哪个生成器 + 封闭参数”，算法本身留在代码注册表里。
    /// </summary>
    public string? Generator { get; init; }

    /// <summary>
    /// 已规范化的生成器参数（<c>k=v;k=v</c>，按键排序）。只允许注册表里该生成器声明的参数；
    /// 未声明生成器时恒为 null，不参与字节与指纹。
    /// </summary>
    public string? GeneratorParams { get; init; }

    /// <summary>
    /// schema v2 引用索引原语：<c>slug</c> / <c>ordinal</c>（出现序号）/ <c>backrefs</c>。
    /// 只读派生值，只能由引用索引提供；null 表示该部件不引用。引用部件必须是叶子。
    /// </summary>
    public string? Ref { get; init; }

    /// <summary>
    /// schema v2 安全策略：<c>link</c>/<c>backlink</c>/<c>image</c>/<c>control</c>/<c>html</c>/<c>text</c>。
    /// 定义只引用策略名，URL 白名单、转义与受控元素实现全部留在代码；null 表示通用部件。
    /// </summary>
    public string? Policy { get; init; }

    /// <summary>
    /// schema v2 原文槽：把块的源文本以<b>转义文本</b>写入该部件内容。
    /// 源文本与 <c>generator</c> 同源（围栏取正文，容器取块体子文本），不裁剪首尾；
    /// 空或纯空白时部件保留但内容为零。原文槽永不解码 HTML，透传受控标签只能走 <see cref="Policy"/>。
    /// </summary>
    public bool Raw { get; init; }

    /// <summary>
    /// schema v2 槽位文本：<c>{field}</c> 形式，取该块<b>已声明属性</b>的值并以转义文本写入。
    /// 与 <see cref="Raw"/> 的区别：<c>raw</c> 取块源文本（正文），<c>text</c> 取节点字段值；
    /// 两者都只输出转义文本，都不解析 HTML；空值/纯空白都零内容（同一口径）。
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// schema v2 受控链接绑定：<c>href="{field}"</c>，只允许出现在 <c>a</c> 部件上。
    /// 值只能来自该块<b>已声明字段</b>，不接受表达式、拼接或模板嵌套；
    /// 渲染期按解码后的协议白名单校验，输出统一走属性上下文转义。
    /// </summary>
    public string? Href { get; init; }

    /// <summary>
    /// schema v2 受控图片绑定：<c>src="{field}"</c>，只允许出现在 <c>img</c> 部件上。
    /// 与 <see cref="Href"/> 同一套字段来源、协议白名单与转义规则。
    /// </summary>
    public string? Src { get; init; }

    /// <summary>已净化的类名列表（空格分隔）；null 表示不输出 class。</summary>
    public string? Class { get; init; }

    /// <summary>
    /// 已规范化的盒子布局声明（<see cref="UserBlockStyleRules"/> 白名单子集）；
    /// null 表示不输出 style 属性。空值不输出，历史定义渲染结果因此保持逐字节不变。
    /// </summary>
    public string? Style { get; init; }

    /// <summary>
    /// schema v3 部件角色：<c>role="root|header|title|icon|content|meta|rail|dot|tail|wrapper"</c>。
    /// 非根部件的角色决定样式作用域与结构约束，缺省按部件名推导。
    /// </summary>
    public string? Role { get; init; }

    /// <summary>schema v3 部件标志：属性写在节点身份/内联样式之后（链接、图片的直出顺序）。</summary>
    public bool AttributesAfterIdentity { get; init; }

    /// <summary>
    /// schema v3 来源子节点条件：<c>source-condition="always|poem-title|…|poem-source-and-note"</c>。
    /// 固定枚举，不命中时本部件及其子树零发射。
    /// </summary>
    public string? SourceCondition { get; init; }

    /// <summary>
    /// schema v3 成员收集：<c>child-collection="timeline-items"</c>；部件只渲染该固定集合的子节点。
    /// </summary>
    public string? ChildCollection { get; init; }

    /// <summary>schema v3 集合存在条件：<c>emit-when-collection="timeline-tags"</c>。</summary>
    public string? EmitWhenCollection { get; init; }

    /// <summary>schema v3 空集合占位的注册表名（如 <c>timeline-empty</c>），HTML 字面量留在代码。</summary>
    public string? EmptyCollection { get; init; }

    /// <summary>
    /// schema v3 命名内容槽：<c>content="inline|text|code-block|tight-children|children|link|footnote-label|backref-icon|space|task-list|nothing"</c>。
    /// 取值是渲染器注册表里的固定写入器名字，定义只声明“用哪个”，写入算法留在代码。
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// schema v3 命名内联样式配方：<c>css="align-block|align-inline|table-row-vars|math-vars|matrix-vars"</c>。
    /// 与 <see cref="Style"/> 的区别：<see cref="Style"/> 是静态白名单声明，本项是按块求值的代码配方。
    /// </summary>
    public string? Css { get; init; }

    /// <summary>
    /// schema v3 命名条件类名配方：<c>class-recipe="language|kind|mermaid-state|matrix-state|qrcode-state|radar-state"</c>。
    /// </summary>
    public string? ClassRecipe { get; init; }

    /// <summary>schema v3 标签发射条件：<c>tag-if="inline-style"</c>；null 表示总是发射标签。</summary>
    public string? TagCondition { get; init; }

    /// <summary>schema v3 部件标志：是否写节点身份属性（默认写）。</summary>
    public bool EmitNodeIdentity { get; init; } = true;

    /// <summary>schema v3 部件标志：以空元素收尾（<c>&lt;br /&gt;</c>）。</summary>
    public bool SelfClosing { get; init; }

    /// <summary>schema v3 部件标志：根部件只承担节点身份，不写实例样式。</summary>
    public bool SuppressNodeStyle { get; init; }

    /// <summary>schema v3 部件标志：非根部件也要写节点实例样式。</summary>
    public bool EmitNodeStyle { get; init; }

    /// <summary>schema v3 部件标志：本部件及其子树零发射（<c>emit="none"</c>）。</summary>
    public bool EmitNone { get; init; }

    /// <summary>
    /// schema v3 显式属性绑定：<c>attr="id@anchor href@href:backlink"</c>。
    /// 名字是输出属性名，来源是注册表里的固定取值器；定义里不出现任何字面量 URL 或脚本。
    /// </summary>
    public IReadOnlyList<UserBlockPartAttribute> Attributes { get; init; } = [];

    public IReadOnlyList<UserBlockPart> Children { get; init; } = [];

    /// <summary>
    /// 源 XML 里该部件实际出现的属性名（小写，<c>attr</c> 绑定记为 <c>"attr"</c>）；
    /// <c>null</c> 表示“未知”，按全部字段都已声明处理（只用于代码内手工构造的部件）。
    /// 继承合并靠它区分“子写了默认值”与“子压根没写”：未写的字段沿用父值；
    /// 净化时必须原样带过去，否则继承会退化成“子覆盖一切”。
    /// </summary>
    public IReadOnlySet<string>? DeclaredFields { get; init; }
}

/// <summary>结构部件上的一条显式属性绑定：输出属性名 + 固定取值来源 + 可选安全策略。</summary>
public sealed record UserBlockPartAttribute(string Name, string Source, string? Policy = null);

/// <summary>结构校验产生的一条问题；由序列化层翻译成诊断，渲染层静默消费。</summary>
public sealed record UserBlockStructureIssue(string Code, string Message);

/// <summary>结构净化结果：净化后的结构与过程中被拒绝的项。</summary>
public sealed record UserBlockStructureSanitizeResult(
    UserBlockStructure Structure,
    IReadOnlyList<UserBlockStructureIssue> Issues)
{
    public static UserBlockStructureSanitizeResult Empty { get; } =
        new(UserBlockStructure.Empty, Array.Empty<UserBlockStructureIssue>());
}

/// <summary>
/// 结构声明白名单与规范化。渲染、序列化与指纹三处共用这一份规则，
/// 保证“定义里写什么”与“输出什么”不会各自解释。
///
/// 安全边界：标签名只允许白名单内的无脚本标签；属性名只允许 <c>class</c> 与
/// <c>style</c>；类名按 token 净化（只留字母、数字、<c>-</c>、<c>_</c>）；
/// <c>style</c> 只保留 <see cref="UserBlockStyleRules"/> 白名单内的盒子属性并规范化。
/// 因此定义里写 <c>script</c>、<c>onclick</c>、<c>url()</c>、<c>expression()</c>
/// 都无路可走——要么被丢弃，要么根本不输出。
/// </summary>
public static class UserBlockStructureRules
{
    /// <summary>纯包裹层的槽位 token：不承载任何内容，只提供一层标签。</summary>
    public const string WrapperPart = "wrapper";

    /// <summary>
    /// <c>&lt;include ref="…"/&gt;</c> 占位节点的 <see cref="UserBlockPart.Part"/> 取值。
    /// 它不参与槽位判定，也不是可渲染槽名；展开后整个节点被片段根部件替换。
    /// </summary>
    public const string IncludePartToken = "include";

    /// <summary>结构最大嵌套深度：超过即丢弃该部件，避免无限深树拖垮渲染。</summary>
    public const int MaxDepth = 8;

    /// <summary>结构最大部件数：超过即丢弃后续部件。</summary>
    public const int MaxParts = 64;

    /// <summary>标签模板字面前缀的长度上限（按展开后的完整标签仍受白名单二次约束）。</summary>
    public const int MaxTagPrefixLength = 16;

    /// <summary>条件表达式两侧的分隔符：只允许相等比较，不提供取反/逻辑组合。</summary>
    public const string WhenSeparator = "==";

    /// <summary>条件右值的长度上限；右值只允许安全 token 字符。</summary>
    public const int MaxWhenValueLength = 32;

    /// <summary>条件类名（<c>name?field==value</c>）的分隔符。</summary>
    public const char ConditionalClassSeparator = '?';

    /// <summary>重复上限缺省值：定义未写 <c>max</c> 时的固定上限。</summary>
    public const int DefaultRepeatMax = 64;

    /// <summary>重复上限的最小值。</summary>
    public const int MinRepeatMax = 1;

    /// <summary>
    /// 允许作为重复来源的派生集合：固定枚举，顺序即诊断文案顺序。
    /// 前五项是解析器派生数据通道（诗句、诗节、图片行、回链、时间线标签）；
    /// <c>children</c>（直属块级子节点）与 <c>body-lines</c>（块源文本按非空行切分）
    /// 是不依赖具体元素类型的通用结构来源。集合名不是属性字段，新增集合名属于机制变更，
    /// 必须单独立项，不能靠定义文件混入。
    /// </summary>
    private static readonly string[] RepeatCollectionOrder =
    [
        "lines",
        "stanzas",
        "images",
        "backrefs",
        "timeline-tags",
        "children",
        "body-lines",
    ];

    private static readonly HashSet<string> AllowedRepeatCollections =
        new(RepeatCollectionOrder, StringComparer.Ordinal);

    /// <summary>
    /// 允许的源数据条件：固定枚举，顺序即诊断文案顺序。只按解析结果判定，
    /// 不接受字段名、表达式、组合或类型名；命中才发射，不命中时本部件及其子树零发射。
    /// <c>body-*</c> 读块源文本（与 <c>raw</c>/<c>generator</c> 同源），<c>children-*</c> 数直属块级子节点，
    /// <c>images-any</c> 复用 <c>repeat="images"</c> 的同一来源。
    /// </summary>
    private static readonly string[] WhenSourceOrder =
    [
        "body-empty",
        "body-any",
        "children-empty",
        "children-any",
        "images-any",
    ];

    private static readonly HashSet<string> AllowedWhenSources =
        new(WhenSourceOrder, StringComparer.Ordinal);

    /// <summary>生成器参数串的长度上限；参数只允许 <c>k=v</c> 以 <c>;</c> 分隔。</summary>
    public const int MaxGeneratorParamsLength = 128;

    /// <summary>
    /// 命名生成器注册表：生成器名 → 允许的参数名 → 值域。
    /// 这是固定枚举原语，新增生成器或参数属于机制变更，必须单独立项，不能靠定义文件混入。
    /// 参数为空表表示该生成器不接受任何参数（源内容走 raw 槽，由 P5 承载）。
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyDictionary<string, GeneratorParameterRule>>
        AllowedGenerators = new(StringComparer.Ordinal)
        {
            ["math"] = new Dictionary<string, GeneratorParameterRule>(StringComparer.Ordinal),
            ["mermaid"] = new Dictionary<string, GeneratorParameterRule>(StringComparer.Ordinal),
            ["image-matrix"] = new Dictionary<string, GeneratorParameterRule>(StringComparer.Ordinal),
            ["qrcode"] = new Dictionary<string, GeneratorParameterRule>(StringComparer.Ordinal)
            {
                ["ecc"] = new(0, 0, new HashSet<string>(StringComparer.Ordinal) { "l", "m", "q", "h" }),
                ["border"] = new(0, 16, null),
            },
            ["radar"] = new Dictionary<string, GeneratorParameterRule>(StringComparer.Ordinal)
            {
                ["labels"] = new(0, 0, new HashSet<string>(StringComparer.Ordinal) { "show", "hide" }),
                ["legend"] = new(0, 0, new HashSet<string>(StringComparer.Ordinal) { "show", "hide" }),
                ["fill"] = new(0, 100, null),
                ["scale"] = new(1, 1000, null),
            },
            ["code-highlight"] = new Dictionary<string, GeneratorParameterRule>(StringComparer.Ordinal)
            {
                // 语言是封闭枚举：定义只能在这几种里选，不能引入任意语法名。
                ["lang"] = new(0, 0, new HashSet<string>(StringComparer.Ordinal)
                {
                    "text",
                    "csharp",
                    "javascript",
                    "json",
                    "xml",
                    "bash",
                }),
                ["linenums"] = new(0, 0, new HashSet<string>(StringComparer.Ordinal) { "on", "off" }),
            },
        };

    /// <summary>
    /// 必填参数：缺失即拒绝保存/加载。code-highlight 必须写明 <c>lang</c>，
    /// 不允许“没写语言就输出无高亮纯文本”的静默降级。
    /// </summary>
    private static readonly Dictionary<string, IReadOnlySet<string>> RequiredGeneratorParams =
        new(StringComparer.Ordinal)
        {
            ["code-highlight"] = new HashSet<string>(StringComparer.Ordinal) { "lang" },
        };

    private readonly record struct GeneratorParameterRule(
        int Min,
        int Max,
        IReadOnlySet<string>? AllowedValues)
    {
        public bool IsValid(string value) => AllowedValues is not null
            ? AllowedValues.Contains(value)
            : int.TryParse(
                  value,
                  NumberStyles.Integer,
                  CultureInfo.InvariantCulture,
                  out var number)
              && number >= Min
              && number <= Max;
    }

    /// <summary>引用索引的固定取值：只读派生值，不接受字段名或表达式。</summary>
    private static readonly HashSet<string> AllowedRefs = new(StringComparer.Ordinal)
    {
        "slug",
        "ordinal",
        "backrefs",
    };

    /// <summary>安全策略的固定取值：定义只引用策略名，实现留在代码。</summary>
    private static readonly HashSet<string> AllowedPolicies = new(StringComparer.Ordinal)
    {
        "link",
        "backlink",
        "image",
        "control",
        "html",
        "text",
    };

    /// <summary>策略允许的标签；受控标签（a/img/input）只有在匹配策略下才放行。</summary>
    private static readonly Dictionary<string, HashSet<string>> PolicyTags =
        new(StringComparer.Ordinal)
        {
            ["link"] = new(StringComparer.Ordinal) { "a" },
            ["backlink"] = new(StringComparer.Ordinal) { "a" },
            ["image"] = new(StringComparer.Ordinal) { "img" },
            ["control"] = new(StringComparer.Ordinal) { "input" },
            ["html"] = new(StringComparer.Ordinal) { "div", "span" },
        };

    /// <summary>受控标签 → 可匹配的策略；没有匹配策略时整部件拒绝。</summary>
    private static readonly Dictionary<string, HashSet<string>> ControlledTagPolicies =
        new(StringComparer.Ordinal)
        {
            ["a"] = new(StringComparer.Ordinal) { "link", "backlink" },
            ["img"] = new(StringComparer.Ordinal) { "image" },
            ["input"] = new(StringComparer.Ordinal) { "control" },
        };

    /// <summary>任何策略都不放行的标签；即使写成定义也直接拒绝。</summary>
    private static readonly HashSet<string> ForbiddenTags = new(StringComparer.Ordinal)
    {
        "script",
        "style",
        "iframe",
        "object",
        "embed",
        "form",
        "textarea",
        "meta",
        "link",
        "base",
        "svg",
    };


    /// <summary>内建槽位：历史七个加上本档新增的三个（footer/badge/meta）。</summary>
    private static readonly HashSet<string> BuiltInParts = new(StringComparer.Ordinal)
    {
        "root",
        "icon",
        "title",
        "title-text",
        "body",
        "content",
        WrapperPart,
        "footer",
        "badge",
        "meta",
    };

    /// <summary>自定义槽名长度上限（含连字符，按规范化后的小写计）。</summary>
    public const int MaxSlotNameLength = 32;

    /// <summary>单个定义最多可声明的自定义槽位数。</summary>
    public const int MaxCustomSlots = 16;

    /// <summary>内建槽位名（只读；新增内建槽位仍属于代码变更，必须单独立项）。</summary>
    public static IReadOnlySet<string> BuiltInSlotNames => BuiltInParts;

    /// <summary>
    /// 允许出现在结构里的标签。全部是无脚本、无外链、无表单能力的排版标签；
    /// script/style/iframe/object/embed/form/input/a/img/link/meta/svg 等一律不在表内。
    /// </summary>
    private static readonly HashSet<string> AllowedTags = new(StringComparer.Ordinal)
    {
        "div", "span", "section", "header", "footer", "main", "article", "aside", "nav",
        "p", "h1", "h2", "h3", "h4", "h5", "h6",
        "strong", "em", "b", "i", "u", "s", "small", "mark", "code", "pre", "br", "hr",
        "del", "ins",
        "ul", "ol", "li", "dl", "dt", "dd",
        "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "colgroup", "col",
        "blockquote", "figure", "figcaption", "details", "summary", "label", "time",
        "abbr", "kbd", "sub", "sup", "q", "cite", "dfn", "var", "samp",
    };

    /// <summary>受控 URL 绑定的固定属性名：只开这两个，不开任意 DOM 属性通道。</summary>
    public const string HrefAttribute = "href";

    /// <summary>受控图片 URL 绑定的固定属性名。</summary>
    public const string SrcAttribute = "src";

    /// <summary>受控 URL 绑定的值长度上限；超长直接拒绝，不做截断。</summary>
    public const int MaxBoundUrlLength = 2048;

    /// <summary>部件上允许出现的属性名；其余一律忽略并报问题。</summary>
    public static IReadOnlySet<string> AllowedPartAttributes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "slot", "tag", "class", "style", "addressable", "when", "when-source", "repeat", "repeat-max", "generator", "generator-params", "ref", "policy", "raw", "text", "href", "src", "content", "css", "class-recipe", "tag-if", "no-identity", "self-closing", "suppress-style", "emit-node-style", "emit", "attr", "role", "attrs-after-identity", "source-condition", "child-collection", "emit-when-collection", "empty-collection" };

    /// <summary>schema v3 命名内容槽：写入器名字的封闭集合。</summary>
    public static IReadOnlySet<string> ContentWriters { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "inline", "text", "code-block", "tight-children", "children",
            "link", "footnote-label", "backref-icon", "space", "task-list", "nothing",
            "current-node-text", "timeline-time", "repeat-item", "poem-meta-break",
            "admonition-icon", "admonition-title-text", "custom-block-icon",
        };

    /// <summary>schema v3 命名内联样式配方：按块求值的封闭集合。</summary>
    public static IReadOnlySet<string> CssRecipes { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "align-block", "align-inline", "table-row-vars", "math-vars", "matrix-vars",
            "accent-adm", "accent-cb", "timeline-vars",
        };

    /// <summary>schema v3 命名条件类名配方：按块求值的封闭集合。</summary>
    public static IReadOnlySet<string> ClassRecipes { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "language", "kind", "mermaid-state", "matrix-state", "qrcode-state", "radar-state",
            "poem-state", "timeline-time-state", "admonition-kind", "kind-minimal", "timeline-layout",
        };

    /// <summary>schema v3 显式属性绑定允许的输出属性名（无事件、无内联样式、无类名通道）。</summary>
    private static readonly HashSet<string> AllowedBoundAttributeNames = new(StringComparer.Ordinal)
    {
        "id", "href", "src", "alt", "title", "loading", "role", "aria-label", "aria-hidden",
        "data-footnote-target", "data-side",
    };

    /// <summary>命名取值规范化委托：命中返回 true 并给出规范名。</summary>
    public delegate bool RecipeNormalizer(string? value, out string normalized);

    /// <summary>命名内容槽规范化：只接受注册表内的写入器名。</summary>
    public static bool TryNormalizeContentWriter(string? value, out string normalized) =>
        TryNormalizeFromSet(value, ContentWriters, out normalized);

    /// <summary>命名样式配方规范化：只接受注册表内的配方名。</summary>
    public static bool TryNormalizeCssRecipe(string? value, out string normalized) =>
        TryNormalizeFromSet(value, CssRecipes, out normalized);

    /// <summary>命名类名配方规范化：只接受注册表内的配方名。</summary>
    public static bool TryNormalizeClassRecipe(string? value, out string normalized) =>
        TryNormalizeFromSet(value, ClassRecipes, out normalized);

    /// <summary>标签发射条件规范化：当前只有 inline-style 一个固定取值。</summary>
    public static bool TryNormalizeTagCondition(string? value, out string normalized) =>
        TryNormalizeFromSet(value, TagConditions, out normalized);

    private static readonly HashSet<string> TagConditions = new(StringComparer.Ordinal) { "inline-style" };

    /// <summary>部件角色名：与渲染层 <c>ElementPartRole</c> 一一对应的固定枚举名（小写）。</summary>
    public static bool TryNormalizePartRole(string? value, out string normalized) =>
        TryNormalizeFromSet(value, PartRoles, out normalized);

    private static readonly HashSet<string> PartRoles = new(StringComparer.Ordinal)
    {
        "root", "header", "title", "icon", "content", "meta", "rail", "dot", "tail", "wrapper",
    };

    /// <summary>结构键：当前只有 <c>poem</c> 放开诗体的两级重复。</summary>
    public static bool TryNormalizeStructureKey(string? value, out string normalized) =>
        TryNormalizeFromSet(value, StructureKeys, out normalized);

    private static readonly HashSet<string> StructureKeys = new(StringComparer.Ordinal) { "poem" };

    /// <summary>来源子节点条件：固定枚举，kebab 名。</summary>
    public static bool TryNormalizeSourceCondition(string? value, out string normalized) =>
        TryNormalizeFromSet(value, SourceConditions, out normalized);

    private static readonly HashSet<string> SourceConditions = new(StringComparer.Ordinal)
    {
        "always", "poem-title", "poem-author", "poem-dynasty", "poem-form",
        "poem-source", "poem-note", "poem-header", "poem-author-line", "poem-source-and-note",
    };

    /// <summary>固定集合名：repeat / child-collection / emit-when-collection 共用。</summary>
    public static bool TryNormalizeCollection(string? value, out string normalized) =>
        TryNormalizeFromSet(value, Collections, out normalized);

    private static readonly HashSet<string> Collections = new(StringComparer.Ordinal)
    {
        "backrefs", "timeline-tags", "timeline-items", "stanzas", "lines",
    };

    /// <summary>结构变体：与渲染层 <c>ElementTemplateVariant</c> 的显式档位一一对应（小写）。</summary>
    public static bool TryNormalizeVariant(string? value, out string normalized) =>
        TryNormalizeFromSet(value, Variants, out normalized);

    /// <summary>
    /// 容器条目行的起始标记（如 <c>@@</c>）：≤4 个非空白、非字母数字字符。
    /// 空值不合法；未声明时由解析器回落 <c>@@</c>，旧行为逐字节不变。
    /// </summary>
    public static bool TryNormalizeItemMarker(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 4)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch) || char.IsLetterOrDigit(ch))
            {
                return false;
            }
        }

        normalized = trimmed;
        return true;
    }

    private static readonly HashSet<string> Variants = new(StringComparer.Ordinal)
    {
        "default", "card", "minimal", "left", "right", "center",
    };

    /// <summary>空集合占位：只接受注册表里的名字，HTML 字面量留在代码。</summary>
    public static bool TryNormalizeEmptyCollection(string? value, out string normalized) =>
        TryNormalizeFromSet(value, EmptyCollections, out normalized);

    private static readonly HashSet<string> EmptyCollections = new(StringComparer.Ordinal) { "timeline-empty" };

    /// <summary>模板选择器：<c>admonition:present|absent</c>（按已声明属性是否存在选结构）。</summary>
    public static bool TryNormalizeSelector(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim().ToLowerInvariant();
        if (text is not ("admonition:present" or "admonition:absent" or "none"))
        {
            return false;
        }

        normalized = text;
        return true;
    }

    private static bool TryNormalizeFromSet(string? value, IReadOnlySet<string> allowed, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim().ToLowerInvariant();
        if (!allowed.Contains(candidate))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    /// <summary>
    /// 显式属性绑定规范化：<c>{source}@{name}</c>，来源是封闭枚举，名字走安全白名单。
    /// 来源里的冒号只用于固定参数（如 <c>node:url:link</c>），不接受任意字段名。
    /// </summary>
    public static bool TryNormalizeAttributeBinding(
        string? token,
        out UserBlockPartAttribute attribute)
    {
        attribute = null!;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var text = token.Trim();
        var at = text.IndexOf('@');
        if (at <= 0 || at == text.Length - 1)
        {
            return false;
        }

        var source = text[..at].Trim().ToLowerInvariant();
        var name = text[(at + 1)..].Trim().ToLowerInvariant();
        if (!AllowedBoundAttributeNames.Contains(name) || !IsKnownAttributeSource(source))
        {
            return false;
        }

        var policy = source switch
        {
            "node:url:link" => "link",
            "node:url:image" => "image",
            "node:href:backlink" => "backlink",
            "ref:backref-href" => "backlink",
            _ => null,
        };

        attribute = new UserBlockPartAttribute(name, source, policy);
        return true;
    }

    private static bool IsKnownAttributeSource(string source) => source switch
    {
        "node-text" or "node:anchor" or "node:id" or "node:url:link" or "node:url:image"
            or "node:href:backlink" or "node:timeline-side-effective"
            or "ref:target" or "ref:backref-href" or "ref:backref-item"
            or "ref:backref-title" or "ref:backref-aria-label" => true,
        _ => IsLiteralAttributeSource(source),
    };

    /// <summary>常量属性值只允许短标识符（如 <c>lazy</c>），不接受引号、空格或任何可执行片段。</summary>
    private static bool IsLiteralAttributeSource(string source)
    {
        const string prefix = "literal:";
        if (!source.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var value = source[prefix.Length..];
        if (value.Length is 0 or > 32)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterLower(ch) && !char.IsAsciiDigit(ch) && ch is not '-' and not '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>槽位 token 规范化；只接受内建槽位（调用方丢弃该部件）。</summary>
    public static bool TryNormalizePart(string? part, out string normalized) =>
        TryNormalizePart(part, null, out normalized);

    /// <summary>
    /// 槽位 token 规范化：内建槽位或定义级已声明的自定义槽位才受理；
    /// <paramref name="customSlots"/> 为 null 表示只看内建槽位。
    /// </summary>
    public static bool TryNormalizePart(
        string? part,
        IReadOnlyCollection<string>? customSlots,
        out string normalized) =>
        TryNormalizePart(part, customSlots, allowUndeclared: false, out normalized);

    /// <summary>
    /// <paramref name="allowUndeclared"/> 为 true 时，格式合法的自定义槽位即使没在本定义声明也受理：
    /// 继承定义的自定义槽位可能只声明在父定义里，读入/写回时要等到合并后再判。
    /// </summary>
    public static bool TryNormalizePart(
        string? part,
        IReadOnlyCollection<string>? customSlots,
        bool allowUndeclared,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(part))
        {
            return false;
        }

        var value = part.Trim().ToLowerInvariant();
        if (!BuiltInParts.Contains(value))
        {
            if (allowUndeclared && TryNormalizeSlotName(value, out var accepted))
            {
                normalized = accepted;
                return true;
            }

            if (customSlots is null || !customSlots.Contains(value))
            {
                return false;
            }
        }

        normalized = value;
        return true;
    }

    /// <summary>槽位名是否内建。</summary>
    public static bool IsBuiltInSlot(string? name) =>
        !string.IsNullOrWhiteSpace(name) && BuiltInParts.Contains(name.Trim().ToLowerInvariant());

    /// <summary>
    /// 自定义槽名格式：小写字母开头，后续小写字母/数字/连字符，长度 1..
    /// <see cref="MaxSlotNameLength"/>。输入先裁剪并转小写。
    /// </summary>
    public static bool TryNormalizeSlotName(string? name, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var value = name.Trim().ToLowerInvariant();
        if (value.Length is 0 or > MaxSlotNameLength || !char.IsAsciiLetterLower(value[0]))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            {
                return false;
            }
        }

        normalized = value;
        return true;
    }

    /// <summary>
    /// 规范化定义级自定义槽位声明：格式非法、与内建槽位冲突、重复、超过上限的条目
    /// 一律丢弃并给出固定诊断码，保留首次出现的合法条目与声明顺序。
    /// </summary>
    public static IReadOnlyList<string> NormalizeSlotDeclarations(
        IEnumerable<string>? slots,
        ICollection<UserBlockStructureIssue>? issues)
    {
        var result = new List<string>();
        if (slots is null)
        {
            return result;
        }

        foreach (var raw in slots)
        {
            if (result.Count >= MaxCustomSlots)
            {
                issues?.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_SLOT_LIMIT",
                    $"自定义槽位超过 {MaxCustomSlots} 个，已丢弃后续声明。"));
                break;
            }

            if (!TryNormalizeSlotName(raw, out var name))
            {
                issues?.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_SLOT_NAME_INVALID",
                    $"自定义槽位名 '{raw}' 非法：需小写字母开头，仅含小写字母、数字或连字符，最长 {MaxSlotNameLength}。"));
                continue;
            }

            if (BuiltInParts.Contains(name))
            {
                issues?.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_SLOT_NAME_RESERVED",
                    $"自定义槽位名 '{name}' 与内建槽位冲突，已忽略该声明。"));
                continue;
            }

            if (result.Contains(name, StringComparer.Ordinal))
            {
                issues?.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_SLOT_DUPLICATE",
                    $"自定义槽位名 '{name}' 重复声明，已保留首次出现的条目。"));
                continue;
            }

            result.Add(name);
        }

        return result;
    }

    /// <summary>
    /// 标签名规范化：白名单内返回 true；schema v2 的受控插值（<c>{field}</c> 或
    /// <c>前缀{field}</c>）按同一入口受理，前缀与字段名都规范化为小写。
    /// 前缀的字符面与展开是否合法由 <see cref="ValidateTagFields"/> 在保存时校验。
    /// </summary>
    public static bool TryNormalizeTag(string? tag, out string normalized) =>
        TryNormalizeTag(tag, null, out normalized);

    /// <summary>
    /// 带策略的标签规范化：受控标签 <c>a</c>/<c>img</c>/<c>input</c> 只有在该策略下才放行；
    /// 其余标签仍走通用白名单。策略本身由 <see cref="TryNormalizePolicy"/> 先行校验。
    /// </summary>
    public static bool TryNormalizeTag(string? tag, string? policy, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var value = tag.Trim();
        if (TryParseTagTemplate(value, out var prefix, out var field))
        {
            normalized = prefix + "{" + field + "}";
            return true;
        }

        value = value.ToLowerInvariant();
        if (AllowedTags.Contains(value))
        {
            normalized = value;
            return true;
        }

        if (policy is not null
            && ControlledTagPolicies.TryGetValue(value, out var policies)
            && policies.Contains(policy))
        {
            normalized = value;
            return true;
        }

        return false;
    }

    /// <summary>引用索引取值规范化：只接受 <c>slug</c>/<c>ordinal</c>/<c>backrefs</c>。</summary>
    public static bool TryNormalizeRef(string? reference, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var value = reference.Trim().ToLowerInvariant();
        if (!AllowedRefs.Contains(value))
        {
            return false;
        }

        normalized = value;
        return true;
    }

    /// <summary>安全策略名规范化：只接受固定枚举内的策略。</summary>
    public static bool TryNormalizePolicy(string? policy, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(policy))
        {
            return false;
        }

        var value = policy.Trim().ToLowerInvariant();
        if (!AllowedPolicies.Contains(value))
        {
            return false;
        }

        normalized = value;
        return true;
    }

    /// <summary>标签是否属于受控元素（<c>a</c>/<c>img</c>/<c>input</c>）。</summary>
    public static bool IsControlledTag(string? tag) =>
        !string.IsNullOrWhiteSpace(tag)
        && ControlledTagPolicies.ContainsKey(tag.Trim().ToLowerInvariant());

    /// <summary>标签是否被任何策略禁止（脚本、样式、嵌入、表单等）。</summary>
    public static bool IsForbiddenTag(string? tag) =>
        !string.IsNullOrWhiteSpace(tag)
        && ForbiddenTags.Contains(tag.Trim().ToLowerInvariant());

    /// <summary>策略是否允许该标签：受控标签必须精确匹配，<c>text</c> 只允许通用白名单标签。</summary>
    public static bool PolicyAllowsTag(string policy, string tag)
    {
        var value = tag.Trim().ToLowerInvariant();
        if (string.Equals(policy, "text", StringComparison.Ordinal))
        {
            return AllowedTags.Contains(value);
        }

        return PolicyTags.TryGetValue(policy, out var tags) && tags.Contains(value);
    }

    /// <summary>
    /// 标签是否为受控插值模板：<c>{field}</c> 或“字面前缀 + 单个 <c>{field}</c>”（如 <c>h{level}</c>）。
    /// 当前只允许一个洞；多个洞、拆段拼接、表达式一律不算模板。
    /// </summary>
    public static bool IsTagExpression(string? tag) => TryParseTagTemplate(tag, out _, out _);

    /// <summary>取插值字段名（小写）；不是合法模板时返回 false。</summary>
    public static bool TryGetTagField(string? tag, out string field)
    {
        if (TryParseTagTemplate(tag, out _, out field))
        {
            return true;
        }

        field = string.Empty;
        return false;
    }

    /// <summary>
    /// 槽位文本模板：只允许<b>纯洞</b> <c>{field}</c>（不接受字面前缀）。
    /// 与标签插值共用字段名规则，但语义是“把字段值写进内容”，不是“拼标签名”。
    /// </summary>
    public static bool TryParseFieldTemplate(string? text, out string field)
    {
        if (TryParseTagTemplate(text, out var prefix, out field) && prefix.Length == 0)
        {
            return true;
        }

        field = string.Empty;
        return false;
    }

    /// <summary>槽位文本引用的是否为已声明字段；保存期校验用。</summary>
    public static IReadOnlyList<UserBlockStructureIssue> ValidateTextField(
        UserBlockStructure? structure,
        IReadOnlyList<UserBlockAttributeDeclaration>? attributes)
    {
        if (structure?.Root is null)
        {
            return [];
        }

        var issues = new List<UserBlockStructureIssue>();
        ValidateTextField(structure.Root, attributes, issues);
        return issues;

        static void ValidateTextField(
            UserBlockPart part,
            IReadOnlyList<UserBlockAttributeDeclaration>? attributes,
            List<UserBlockStructureIssue> issues)
        {
            if (part.Text is not null && TryParseFieldTemplate(part.Text, out var field))
            {
                var declared = attributes is not null
                    && attributes.Any(attribute =>
                        string.Equals(attribute.Key, field, StringComparison.OrdinalIgnoreCase));
                if (!declared)
                {
                    issues.Add(new UserBlockStructureIssue(
                        "USER_ELEMENTS_STRUCTURE_TEXT_FIELD_UNKNOWN",
                        $"结构部件 '{part.Part}' 的槽位文本引用了未声明字段 '{field}'，运行时将输出空内容。"));
                }
            }

            foreach (var child in part.Children)
            {
                ValidateTextField(child, attributes, issues);
            }
        }
    }

    /// <summary>
    /// 受控 URL 绑定的声明规范化：<c>href</c> 只允许配 <c>a</c>，<c>src</c> 只允许配 <c>img</c>；
    /// 取值必须是纯洞 <c>{field}</c>。只做声明层校验，URL 值的安全校验在渲染期按解码后结果做。
    /// </summary>
    public static bool TryNormalizeBoundAttribute(
        string? tag,
        string? attributeName,
        string? template,
        out string normalized,
        out string? errorCode)
    {
        normalized = string.Empty;
        errorCode = null;

        var name = attributeName?.Trim().ToLowerInvariant();
        if (name is not (HrefAttribute or SrcAttribute))
        {
            errorCode = "USER_ELEMENTS_STRUCTURE_BIND_ATTRIBUTE_UNKNOWN";
            return false;
        }

        var expectedTag = string.Equals(name, HrefAttribute, StringComparison.Ordinal) ? "a" : "img";
        var actualTag = tag?.Trim().ToLowerInvariant();
        if (!string.Equals(actualTag, expectedTag, StringComparison.Ordinal))
        {
            errorCode = "USER_ELEMENTS_STRUCTURE_BIND_TAG_MISMATCH";
            return false;
        }

        if (!TryParseFieldTemplate(template, out var field))
        {
            errorCode = "USER_ELEMENTS_STRUCTURE_BIND_TEMPLATE_INVALID";
            return false;
        }

        normalized = "{" + field + "}";
        return true;
    }

    /// <summary>
    /// 保存期校验：受控 URL 绑定必须与标签配对、必须是 <c>{field}</c> 纯洞模板、
    /// 字段必须已声明；<c>img</c> 的绑定部件必须是叶子。任一不满足即整条拒绝。
    /// </summary>
    public static IReadOnlyList<UserBlockStructureIssue> ValidateBindingFields(
        UserBlockStructure? structure,
        IReadOnlyList<UserBlockAttributeDeclaration>? attributes)
    {
        if (structure?.Root is null)
        {
            return [];
        }

        var issues = new List<UserBlockStructureIssue>();
        CollectBindingIssues(structure.Root, attributes, issues);
        return issues;

        static void CollectBindingIssues(
            UserBlockPart part,
            IReadOnlyList<UserBlockAttributeDeclaration>? attributes,
            List<UserBlockStructureIssue> issues)
        {
            ValidateBound(part, HrefAttribute, part.Href, attributes, issues);
            ValidateBound(part, SrcAttribute, part.Src, attributes, issues);

            if (part.Src is not null && part.Children.Count > 0)
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_BIND_REQUIRES_LEAF",
                    $"结构部件 '{part.Part}' 声明了图片地址绑定但仍含子部件：img 必须是叶子。"));
            }

            foreach (var child in part.Children)
            {
                CollectBindingIssues(child, attributes, issues);
            }
        }

        static void ValidateBound(
            UserBlockPart part,
            string attributeName,
            string? template,
            IReadOnlyList<UserBlockAttributeDeclaration>? attributes,
            List<UserBlockStructureIssue> issues)
        {
            if (template is null)
            {
                return;
            }

            if (!TryNormalizeBoundAttribute(part.Tag, attributeName, template, out var normalized, out var errorCode))
            {
                issues.Add(new UserBlockStructureIssue(
                    errorCode!,
                    $"结构部件 '{part.Part}' 的 {attributeName} 绑定 '{template}' 非法："
                    + (string.Equals(attributeName, HrefAttribute, StringComparison.Ordinal)
                        ? "href 只允许出现在 a 部件上。"
                        : "src 只允许出现在 img 部件上。")));
                return;
            }

            TryParseFieldTemplate(normalized, out var field);
            var declared = attributes is not null
                && attributes.Any(attribute =>
                    string.Equals(attribute.Key, field, StringComparison.OrdinalIgnoreCase));
            if (!declared)
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_BIND_FIELD_UNKNOWN",
                    $"结构部件 '{part.Part}' 的 {attributeName} 绑定引用了未声明字段 '{field}'。"));
            }
        }
    }

    /// <summary>
    /// 受控 URL 绑定的值检查：先做有界解码（HTML 实体 → 百分号，循环到稳定），
    /// 再剔除控制/格式/空白字符，最后只按<b>规范化后的协议</b>判定。
    /// 允许 <c>http</c>/<c>https</c>/<c>mailto</c>、相对路径与锚点 <c>#</c>；
    /// 其余协议（<c>javascript</c>/<c>data</c>/<c>vbscript</c>/<c>file</c> 等）一律拒绝。
    /// 超长值直接拒绝，不做截断。
    /// </summary>
    public static bool IsSafeBoundUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxBoundUrlLength)
        {
            return false;
        }

        var decoded = value;
        for (var pass = 0; pass < 4; pass++)
        {
            var next = WebUtility.HtmlDecode(decoded);
            next = TryUnescapePercent(next);
            if (string.Equals(next, decoded, StringComparison.Ordinal))
            {
                break;
            }

            decoded = next;
            if (decoded.Length > MaxBoundUrlLength)
            {
                return false;
            }
        }

        var probe = new StringBuilder(decoded.Length);
        foreach (var c in decoded)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c)
                || category is UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.SpaceSeparator
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            probe.Append(c);
        }

        if (probe.Length == 0 || probe.Length > MaxBoundUrlLength)
        {
            return false;
        }

        var normalized = probe.ToString();

        // 协议相对地址（//host）与反斜杠变形都按外部地址处理，不在白名单内。
        if (normalized.StartsWith("//", StringComparison.Ordinal)
            || normalized.Contains('\\'))
        {
            return false;
        }

        var colon = normalized.IndexOf(':');
        if (colon >= 0)
        {
            var pathStart = FirstIndex(normalized, '/', '?', '#');
            if (pathStart < 0 || colon < pathStart)
            {
                var scheme = normalized[..colon];
                if (!IsValidSchemeToken(scheme))
                {
                    return false;
                }

                return scheme.ToLowerInvariant() is "http" or "https" or "mailto";
            }
        }

        // 没有协议：相对路径、锚点、查询串都按相对地址放行。
        return true;

        static int FirstIndex(string text, params char[] chars)
        {
            var result = -1;
            foreach (var c in chars)
            {
                var index = text.IndexOf(c);
                if (index >= 0 && (result < 0 || index < result))
                {
                    result = index;
                }
            }

            return result;
        }

        static bool IsValidSchemeToken(string scheme) =>
            scheme.Length > 0
            && char.IsAsciiLetter(scheme[0])
            && scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');

        static string TryUnescapePercent(string text)
        {
            try
            {
                return Uri.UnescapeDataString(text);
            }
            catch (UriFormatException)
            {
                return text;
            }
        }
    }

    /// <summary>取模板字面前缀（小写）；静态标签返回 false，空前缀返回 true 且为空串。</summary>
    public static bool TryGetTagPrefix(string? tag, out string prefix)    {
        if (TryParseTagTemplate(tag, out prefix, out _))
        {
            return true;
        }

        prefix = string.Empty;
        return false;
    }

    /// <summary>标签前缀允许的字符：字母开头，后续字母/数字/<c>-</c>；语义上限见 <see cref="MaxTagPrefixLength"/>。</summary>
    public static bool IsValidTagPrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return true;
        }

        if (!char.IsAsciiLetter(prefix[0]))
        {
            return false;
        }

        foreach (var c in prefix)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 条件表达式是否是合法形态：<c>{field}=={value}</c>。
    /// 字段名按属性键规则（字母开头、字母/数字/<c>-</c>/<c>_</c>，最长 64）；
    /// 右值只允许字母、数字、<c>-</c>、<c>_</c>（最长 <see cref="MaxWhenValueLength"/>），
    /// 因此不可能拼出表达式、取反、函数调用或脚本。
    /// </summary>
    public static bool TryParseWhen(string? when, out string field, out string value)
    {
        field = string.Empty;
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(when))
        {
            return false;
        }

        var text = when.Trim();
        var separator = text.IndexOf(WhenSeparator, StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        // 只允许一个比较符：字段名与右值都不允许再出现 '='，避免拼出链式条件。
        if (text.IndexOf(WhenSeparator, separator + WhenSeparator.Length, StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        var fieldText = text[..separator];
        var valueText = text[(separator + WhenSeparator.Length)..];
        if (!IsSafeWhenField(fieldText) || !IsSafeWhenValue(valueText))
        {
            return false;
        }

        field = fieldText.ToLowerInvariant();
        value = valueText;
        return true;
    }

    /// <summary>条件表达式是否合法；不做字段是否已声明的判断（那属于保存期校验）。</summary>
    public static bool IsWhenExpression(string? when) => TryParseWhen(when, out _, out _);

    /// <summary>规范化条件表达式（字段小写、去空白）；非法时返回 null。</summary>
    public static string? NormalizeWhen(string? when) =>
        TryParseWhen(when, out var field, out var value) ? field + WhenSeparator + value : null;

    /// <summary>重复集合名规范化：只接受固定枚举内的派生集合，按小写返回。</summary>
    public static bool TryNormalizeRepeat(string? repeat, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(repeat))
        {
            return false;
        }

        var value = repeat.Trim().ToLowerInvariant();
        if (!AllowedRepeatCollections.Contains(value))
        {
            return false;
        }

        normalized = value;
        return true;
    }

    /// <summary>重复上限是否在 1..<see cref="DefaultRepeatMax"/> 之间。</summary>
    public static bool IsValidRepeatMax(int max) => max is >= MinRepeatMax and <= DefaultRepeatMax;

    /// <summary>允许的重复集合名（只读；供文档与测试核对，不接受定义侧扩展）。</summary>
    public static IReadOnlySet<string> RepeatCollections => AllowedRepeatCollections;

    /// <summary>允许的重复集合名，按诊断文案顺序排列。</summary>
    public static IReadOnlyList<string> RepeatCollectionNames => RepeatCollectionOrder;

    /// <summary>源数据条件规范化：只接受固定枚举内的取值，按小写返回。</summary>
    public static bool TryNormalizeWhenSource(string? condition, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(condition))
        {
            return false;
        }

        var value = condition.Trim().ToLowerInvariant();
        if (!AllowedWhenSources.Contains(value))
        {
            return false;
        }

        normalized = value;
        return true;
    }

    /// <summary>允许的源数据条件取值集合（只读；供文档与测试核对，不接受定义侧扩展）。</summary>
    public static IReadOnlySet<string> WhenSources => AllowedWhenSources;

    /// <summary>允许的源数据条件，按诊断文案顺序排列。</summary>
    public static IReadOnlyList<string> WhenSourceNames => WhenSourceOrder;

    /// <summary>
    /// 保存期校验：源数据条件必须是固定枚举值，且不能与字段条件 <c>when</c> 同时声明——
    /// 一个部件只有一个条件槽位，叠加会变成隐式 AND。任一不满足即整条拒绝。
    /// </summary>
    public static IReadOnlyList<UserBlockStructureIssue> ValidateWhenSourceFields(UserBlockStructure? structure)
    {
        if (structure?.Root is null)
        {
            return Array.Empty<UserBlockStructureIssue>();
        }

        var issues = new List<UserBlockStructureIssue>();
        CollectWhenSourceIssues(structure.Root, issues);
        return issues;
    }

    private static void CollectWhenSourceIssues(UserBlockPart part, List<UserBlockStructureIssue> issues)
    {
        if (part.WhenSource is not null)
        {
            if (!TryNormalizeWhenSource(part.WhenSource, out _))
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_WHEN_SOURCE_UNKNOWN",
                    $"结构部件 '{part.Part}' 的源数据条件 '{part.WhenSource}' 不在允许的枚举内"
                    + $"（{string.Join('、', WhenSourceOrder)}）。"));
            }
            else if (part.When is not null)
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_WHEN_SOURCE_CONFLICT",
                    $"结构部件 '{part.Part}' 同时声明了字段条件 '{part.When}' 与源数据条件 "
                    + $"'{part.WhenSource}'：一个部件只允许一个条件槽位。"));
            }
        }

        foreach (var child in part.Children)
        {
            CollectWhenSourceIssues(child, issues);
        }
    }

    /// <summary>生成器名规范化：只接受固定注册表内的名字（按小写返回）。</summary>
    public static bool TryNormalizeGenerator(string? generator, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(generator))
        {
            return false;
        }

        var value = generator.Trim().ToLowerInvariant();
        if (!AllowedGenerators.ContainsKey(value))
        {
            return false;
        }

        normalized = value;
        return true;
    }

    /// <summary>注册表内的生成器名（只读；供文档与测试核对，不接受定义侧扩展）。</summary>
    public static IReadOnlyCollection<string> GeneratorNames => AllowedGenerators.Keys;

    /// <summary>
    /// <c>code-highlight</c> 的封闭语言枚举判定：渲染器与校验共用同一份值域，
    /// 不在枚举内的语言不会被当成“无高亮纯文本”继续输出。
    /// </summary>
    public static bool IsCodeHighlightLanguage(string? language)
    {
        return !string.IsNullOrWhiteSpace(language)
               && AllowedGenerators.TryGetValue("code-highlight", out var rules)
               && rules.TryGetValue("lang", out var rule)
               && rule.AllowedValues is { } values
               && values.Contains(language.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// 规范化生成器参数：只允许该生成器声明的参数名，值按枚举或整数区间校验；
    /// 按键排序输出（确定性）。未知键、重复键、值越界、格式错误一律拒绝并给出固定诊断码。
    /// </summary>
    public static bool TryNormalizeGeneratorParams(
        string? generator,
        string? parameters,
        out string? normalized,
        out string? errorCode)
    {
        normalized = null;
        errorCode = null;
        if (string.IsNullOrWhiteSpace(generator)
            || !AllowedGenerators.TryGetValue(generator, out var rules))
        {
            errorCode = "USER_ELEMENTS_STRUCTURE_GENERATOR_UNKNOWN";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parameters))
        {
            if (RequiredGeneratorParams.TryGetValue(generator, out var requiredWhenEmpty)
                && requiredWhenEmpty.Count > 0)
            {
                errorCode = "USER_ELEMENTS_STRUCTURE_GENERATOR_PARAM_REQUIRED";
                return false;
            }

            return true;
        }

        var pairs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in parameters.Split(';'))
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            var separator = token.IndexOf('=');
            if (separator <= 0)
            {
                errorCode = "USER_ELEMENTS_STRUCTURE_GENERATOR_PARAM_MALFORMED";
                return false;
            }

            var key = token[..separator].Trim().ToLowerInvariant();
            var value = token[(separator + 1)..].Trim().ToLowerInvariant();
            if (!rules.TryGetValue(key, out var rule))
            {
                errorCode = "USER_ELEMENTS_STRUCTURE_GENERATOR_PARAM_UNKNOWN";
                return false;
            }

            if (pairs.ContainsKey(key))
            {
                errorCode = "USER_ELEMENTS_STRUCTURE_GENERATOR_PARAM_DUPLICATE";
                return false;
            }

            if (!rule.IsValid(value))
            {
                errorCode = "USER_ELEMENTS_STRUCTURE_GENERATOR_PARAM_VALUE_INVALID";
                return false;
            }

            pairs[key] = value;
        }

        if (RequiredGeneratorParams.TryGetValue(generator, out var required))
        {
            foreach (var key in required)
            {
                if (!pairs.ContainsKey(key))
                {
                    errorCode = "USER_ELEMENTS_STRUCTURE_GENERATOR_PARAM_REQUIRED";
                    return false;
                }
            }
        }

        if (pairs.Count == 0)
        {
            return true;
        }

        var builder = new StringBuilder();
        foreach (var pair in pairs)
        {
            if (builder.Length > 0)
            {
                builder.Append(';');
            }

            builder.Append(pair.Key).Append('=').Append(pair.Value);
        }

        var text = builder.ToString();
        if (text.Length > MaxGeneratorParamsLength)
        {
            errorCode = "USER_ELEMENTS_STRUCTURE_GENERATOR_PARAM_MALFORMED";
            return false;
        }

        normalized = text;
        return true;
    }

    private static bool IsSafeWhenField(string field)
    {
        if (field.Length is 0 or > 64 || !char.IsAsciiLetter(field[0]))
        {
            return false;
        }

        foreach (var c in field)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeWhenValue(string value)
    {
        if (value.Length is 0 or > MaxWhenValueLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 拆分条件类名：<c>name?field==value</c>。普通类名返回 false 且 <paramref name="condition"/> 为空。
    /// 条件类名与条件部件共用同一条件文法与校验码。
    /// </summary>
    public static bool TrySplitConditionalClassToken(
        string? token,
        out string name,
        out string condition)
    {
        name = token ?? string.Empty;
        condition = string.Empty;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var separator = token.IndexOf(ConditionalClassSeparator);
        if (separator < 0)
        {
            return false;
        }

        name = token[..separator];
        condition = token[(separator + 1)..];
        return true;
    }

    /// <summary>
    /// 去掉类名里的条件后缀，只保留无条件 token。求值消费落地前，
    /// 任何投影（图/模板）都必须经这里过滤，绝不把 <c>?field==value</c> 写进 HTML class。
    /// </summary>
    public static string? StripConditionalClasses(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return null;
        }

        var tokens = new List<string>();
        foreach (var token in className.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TrySplitConditionalClassToken(token, out _, out var condition) || condition.Length == 0)
            {
                tokens.Add(token);
            }
        }

        return tokens.Count == 0 ? null : string.Join(' ', tokens);
    }

    /// <summary>枚举类名中的全部 token（含条件 token），供保存期校验逐条检查条件。</summary>
    public static IReadOnlyList<string> EnumerateClassTokens(string? className) =>
        string.IsNullOrWhiteSpace(className)
            ? Array.Empty<string>()
            : className.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool TryParseTagTemplate(string? tag, out string prefix, out string field)
    {
        prefix = string.Empty;
        field = string.Empty;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var value = tag.Trim();
        var open = value.IndexOf('{');
        if (open < 0 || value.IndexOf('{', open + 1) >= 0)
        {
            return false;
        }

        // 洞必须在末尾：前缀只能是字面量，洞后不允许再拼接。
        if (value.IndexOf('}') != value.Length - 1)
        {
            return false;
        }

        var name = value[(open + 1)..^1];
        if (name.Length is 0 or > 64 || !char.IsAsciiLetter(name[0]))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        var literal = value[..open];
        // 净化层只保证文本语法可逆（不含分段符、尖括号、引号等）；
        // 前缀的语义字符面与长度由 ValidateTagFields 阻断保存。
        if (literal.Length > MaxTagPrefixLength * 4)
        {
            return false;
        }

        foreach (var c in literal)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        prefix = literal.ToLowerInvariant();
        field = name.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// 保存期校验：每个插值都必须命中已声明的枚举字段；前缀必须是安全字符且不超长；
    /// 字段的每个允许值与前缀拼出的完整标签都必须落在标签白名单内。
    /// 任一条件不满足即整条拒绝（调用方不得降级为“保留其余部件”）。
    /// </summary>
    public static IReadOnlyList<UserBlockStructureIssue> ValidateTagFields(
        UserBlockStructure? structure,
        IReadOnlyList<UserBlockAttributeDeclaration>? attributes)
    {
        if (structure?.Root is null)
        {
            return Array.Empty<UserBlockStructureIssue>();
        }

        var byKey = new Dictionary<string, UserBlockAttributeDeclaration>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in attributes ?? [])
        {
            byKey.TryAdd(attribute.Key, attribute);
        }

        var issues = new List<UserBlockStructureIssue>();
        CollectTagFieldIssues(structure.Root, byKey, issues);
        return issues;
    }

    private static void CollectTagFieldIssues(
        UserBlockPart part,
        IReadOnlyDictionary<string, UserBlockAttributeDeclaration> attributes,
        List<UserBlockStructureIssue> issues)
    {
        // 含花括号但拼不成模板：多个洞、洞不在末尾、字段名非法——保存前阻断，不能静默丢部件。
        if (!TryParseTagTemplate(part.Tag, out var prefix, out var field))
        {
            if (part.Tag is { } tagText && (tagText.Contains('{') || tagText.Contains('}')))
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_TAG_TEMPLATE_INVALID",
                    $"结构部件 '{part.Part}' 的标签模板 '{part.Tag}' 非法：只允许“字面前缀 + 单个 {{field}}”且洞必须在末尾。"));
            }
        }
        else
        {
            if (!IsValidTagPrefix(prefix))
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_TAG_PREFIX_INVALID",
                    $"结构部件 '{part.Part}' 的标签前缀 '{prefix}' 含非法字符：只允许字母、数字与 '-'，且以字母开头。"));
            }
            else if (prefix.Length > MaxTagPrefixLength)
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_TAG_PREFIX_TOO_LONG",
                    $"结构部件 '{part.Part}' 的标签前缀 '{prefix}' 超过 {MaxTagPrefixLength} 个字符。"));
            }
            else if (!attributes.TryGetValue(field, out var declaration))
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_TAG_FIELD_UNKNOWN",
                    $"结构部件 '{part.Part}' 的标签插值 '{{{field}}}' 未在属性声明中定义。"));
            }
            else if (declaration.Kind != UserBlockAttributeKind.Enum || declaration.AllowedValues.Count == 0)
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_TAG_FIELD_NOT_ENUM",
                    $"结构部件 '{part.Part}' 的标签插值 '{{{field}}}' 要求该字段声明为带允许值的 Enum。"));
            }
            else
            {
                foreach (var value in declaration.AllowedValues)
                {
                    if (!TryNormalizeTag(prefix + value.Trim(), out var tag) || IsTagExpression(tag))
                    {
                        issues.Add(new UserBlockStructureIssue(
                            "USER_ELEMENTS_STRUCTURE_TAG_VALUE_INVALID",
                            $"结构部件 '{part.Part}' 的标签插值 '{prefix}{{{field}}}' 的允许值 '{value}' 展开后不在标签白名单内。"));
                        break;
                    }
                }
            }
        }

        foreach (var child in part.Children)
        {
            CollectTagFieldIssues(child, attributes, issues);
        }
    }

    /// <summary>
    /// 保存期校验：条件部件与条件类名的条件必须命中已声明枚举字段，且右值在枚举内。
    /// 条件形态非法同样整条拒绝，不允许降级为“忽略该条件”。
    /// </summary>
    public static IReadOnlyList<UserBlockStructureIssue> ValidateWhenFields(
        UserBlockStructure? structure,
        IReadOnlyList<UserBlockAttributeDeclaration>? attributes)
    {
        if (structure?.Root is null)
        {
            return Array.Empty<UserBlockStructureIssue>();
        }

        var byKey = new Dictionary<string, UserBlockAttributeDeclaration>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in attributes ?? [])
        {
            byKey.TryAdd(attribute.Key, attribute);
        }

        var issues = new List<UserBlockStructureIssue>();
        CollectWhenIssues(structure.Root, byKey, issues);
        return issues;
    }

    private static void CollectWhenIssues(
        UserBlockPart part,
        IReadOnlyDictionary<string, UserBlockAttributeDeclaration> attributes,
        List<UserBlockStructureIssue> issues)
    {
        if (part.When is not null)
        {
            CollectSingleWhenIssue(part.When, $"结构部件 '{part.Part}'", attributes, issues);
        }

        foreach (var token in EnumerateClassTokens(part.Class))
        {
            if (!TrySplitConditionalClassToken(token, out var name, out var condition))
            {
                continue;
            }

            if (!IsSafeClassToken(name))
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_CLASS_INVALID",
                    $"条件类名 '{token}' 的类名部分非法：只允许字母、数字、'-' 与 '_'。"));
                continue;
            }

            CollectSingleWhenIssue(condition, $"结构部件 '{part.Part}' 的类名 '{name}'", attributes, issues);
        }

        foreach (var child in part.Children)
        {
            CollectWhenIssues(child, attributes, issues);
        }
    }

    private static void CollectSingleWhenIssue(
        string when,
        string target,
        IReadOnlyDictionary<string, UserBlockAttributeDeclaration> attributes,
        List<UserBlockStructureIssue> issues)
    {
        if (!TryParseWhen(when, out var field, out var value))
        {
            issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_WHEN_TEMPLATE_INVALID",
                $"{target} 的条件 '{when}' 非法：只允许 \"字段==枚举值\"，两侧只能用字母、数字、'-' 与 '_'。"));
            return;
        }

        if (!attributes.TryGetValue(field, out var declaration))
        {
            issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_WHEN_FIELD_UNKNOWN",
                $"{target} 的条件字段 '{field}' 未在属性声明中定义。"));
            return;
        }

        if (declaration.Kind != UserBlockAttributeKind.Enum || declaration.AllowedValues.Count == 0)
        {
            issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_WHEN_FIELD_NOT_ENUM",
                $"{target} 的条件字段 '{field}' 要求该字段声明为带允许值的 Enum。"));
            return;
        }

        if (!declaration.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_WHEN_VALUE_INVALID",
                $"{target} 的条件值 '{value}' 不在字段 '{field}' 的允许值内（{string.Join('、', declaration.AllowedValues)}）。"));
        }
    }

    /// <summary>
    /// 保存期校验：重复集合必须是固定枚举内的派生集合，上限必须在 1..64，
    /// 不允许嵌套重复（只允许单层），也不允许重复根部件（会产生多个根，结构无法表达）。
    /// 任一不满足即整条拒绝，不允许降级为“忽略重复”。
    /// </summary>
    public static IReadOnlyList<UserBlockStructureIssue> ValidateRepeatFields(
        UserBlockStructure? structure,
        string? structureKey = null)
    {
        if (structure?.Root is null)
        {
            return Array.Empty<UserBlockStructureIssue>();
        }

        var issues = new List<UserBlockStructureIssue>();
        // 结构键声明了诗体时放行固定的一层嵌套（stanzas → lines）；其余结构仍只允许单层重复。
        var allowNested = string.Equals(structureKey, "poem", StringComparison.Ordinal);
        CollectRepeatIssues(structure.Root, repeatDepth: 0, allowNested, issues);
        return issues;
    }

    private static void CollectRepeatIssues(
        UserBlockPart part,
        int repeatDepth,
        bool allowNested,
        List<UserBlockStructureIssue> issues)
    {
        var hasRepeat = part.Repeat is not null;
        if (hasRepeat)
        {
            if (!TryNormalizeRepeat(part.Repeat, out var collection))
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_REPEAT_COLLECTION_UNKNOWN",
                    $"结构部件 '{part.Part}' 的重复集合 '{part.Repeat}' 不在允许的派生集合内（{string.Join('、', RepeatCollectionOrder)}）。"));
            }
            else if (!IsValidRepeatMax(part.RepeatMax))
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_REPEAT_MAX_INVALID",
                    $"结构部件 '{part.Part}' 的重复上限 {part.RepeatMax} 越界：允许 {MinRepeatMax}~{DefaultRepeatMax}。"));
            }
            else if (repeatDepth > (allowNested ? 1 : 0))
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_REPEAT_NESTED",
                    $"结构部件 '{part.Part}' 在重复部件内再次声明重复（{collection}）：只允许单层重复。"));
            }
            else if (string.Equals(part.Part, "root", StringComparison.Ordinal))
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_REPEAT_ROOT",
                    "结构根部件不允许重复：一根结构只能有一个顶层部件。"));
            }
        }

        foreach (var child in part.Children)
        {
            CollectRepeatIssues(child, repeatDepth + (hasRepeat ? 1 : 0), allowNested, issues);
        }
    }

    /// <summary>
    /// 保存期校验：生成器必须在固定注册表内，参数必须是该生成器声明的键与值域；
    /// 参数不能脱离生成器单独存在。任一不满足即整条拒绝，不允许降级为“忽略参数”。
    /// </summary>
    public static IReadOnlyList<UserBlockStructureIssue> ValidateGeneratorFields(UserBlockStructure? structure)
    {
        if (structure?.Root is null)
        {
            return Array.Empty<UserBlockStructureIssue>();
        }

        var issues = new List<UserBlockStructureIssue>();
        CollectGeneratorIssues(structure.Root, issues);
        return issues;
    }

    private static void CollectGeneratorIssues(UserBlockPart part, List<UserBlockStructureIssue> issues)
    {
        if (!string.IsNullOrWhiteSpace(part.Generator))
        {
            if (!TryNormalizeGenerator(part.Generator, out var generator))
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_GENERATOR_UNKNOWN",
                    $"结构部件 '{part.Part}' 的生成器 '{part.Generator}' 不在允许的注册表内。"));
            }
            else if (!TryNormalizeGeneratorParams(
                         generator,
                         part.GeneratorParams,
                         out _,
                         out var errorCode))
            {
                issues.Add(new UserBlockStructureIssue(
                    errorCode!,
                    $"结构部件 '{part.Part}' 的生成器参数 '{part.GeneratorParams}' 非法："
                    + "只允许该生成器声明的参数名与封闭值域。"));
            }
        }
        else if (!string.IsNullOrWhiteSpace(part.GeneratorParams))
        {
            issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_GENERATOR_PARAMS_WITHOUT_GENERATOR",
                $"结构部件 '{part.Part}' 写了生成器参数但没有声明生成器。"));
        }

        foreach (var child in part.Children)
        {
            CollectGeneratorIssues(child, issues);
        }
    }

    /// <summary>
    /// 保存期校验：引用值只能是 <c>slug</c>/<c>ordinal</c>/<c>backrefs</c>，且引用部件必须是叶子。
    /// 任一不满足即整条拒绝，不允许降级为“忽略引用”。
    /// </summary>
    public static IReadOnlyList<UserBlockStructureIssue> ValidateRefFields(UserBlockStructure? structure)
    {
        if (structure?.Root is null)
        {
            return Array.Empty<UserBlockStructureIssue>();
        }

        var issues = new List<UserBlockStructureIssue>();
        CollectRefIssues(structure.Root, issues);
        return issues;
    }

    private static void CollectRefIssues(UserBlockPart part, List<UserBlockStructureIssue> issues)
    {
        if (!string.IsNullOrWhiteSpace(part.Ref))
        {
            if (!TryNormalizeRef(part.Ref, out _))
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_REF_UNKNOWN",
                    $"结构部件 '{part.Part}' 的引用值 '{part.Ref}' 不在允许的枚举内。"));
            }
            else if (part.Children.Count > 0)
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_REF_REQUIRES_LEAF",
                    $"结构部件 '{part.Part}' 声明了引用 {part.Ref} 但仍含子部件：引用部件必须是叶子。"));
            }
        }

        foreach (var child in part.Children)
        {
            CollectRefIssues(child, issues);
        }
    }

    /// <summary>
    /// 保存期校验：策略必须在固定枚举内，标签必须被该策略允许，受控标签不得缺少策略，
    /// 脚本/样式/嵌入/表单类标签任何策略都不放行。任一不满足即整条拒绝，宁可拒绝也不放行。
    /// </summary>
    public static IReadOnlyList<UserBlockStructureIssue> ValidatePolicyFields(UserBlockStructure? structure)
    {
        if (structure?.Root is null)
        {
            return Array.Empty<UserBlockStructureIssue>();
        }

        var issues = new List<UserBlockStructureIssue>();
        CollectPolicyIssues(structure.Root, issues);
        return issues;
    }

    private static void CollectPolicyIssues(UserBlockPart part, List<UserBlockStructureIssue> issues)
    {
        if (IsForbiddenTag(part.Tag))
        {
            issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_POLICY_TAG_FORBIDDEN",
                $"结构标签 '{part.Tag}' 被安全策略禁止。"));
        }
        else if (!string.IsNullOrWhiteSpace(part.Policy))
        {
            if (!TryNormalizePolicy(part.Policy, out var policy))
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_POLICY_UNKNOWN",
                    $"结构部件 '{part.Part}' 的安全策略 '{part.Policy}' 不在允许的枚举内。"));
            }
            else if (part.Tag is not null && !PolicyAllowsTag(policy, part.Tag))
            {
                issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_POLICY_TAG_MISMATCH",
                    $"安全策略 '{policy}' 不允许标签 '{part.Tag}'。"));
            }
        }
        else if (IsControlledTag(part.Tag))
        {
            issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_POLICY_TAG_MISMATCH",
                $"受控标签 '{part.Tag}' 必须声明匹配的安全策略（a→link/backlink、img→image、input→control）。"));
        }

        foreach (var child in part.Children)
        {
            CollectPolicyIssues(child, issues);
        }
    }

    /// <summary>
    /// 类名净化：按空白切 token，只保留 <c>[A-Za-z0-9_-]</c> 组成的 token，
    /// 去重且保持书写顺序。含引号、尖括号、分号等字符的 token 会被整段丢弃。
    /// </summary>
    public static string? SanitizeClass(string? value, ICollection<string>? rejected = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryNormalizeClassToken(raw, out var token))
            {
                rejected?.Add(raw);
                continue;
            }

            if (seen.Add(token))
            {
                tokens.Add(token);
            }
        }

        return tokens.Count == 0 ? null : string.Join(' ', tokens);
    }

    /// <summary>
    /// 单个类名 token 的规范化：普通 token 原样保留；条件 token
    /// <c>name?field==value</c> 的类名部分按类名规则净化、条件部分按条件文法规范化。
    /// 任一非法即整 token 拒绝。
    /// </summary>
    private static bool TryNormalizeClassToken(string raw, out string token)
    {
        token = raw;
        if (!TrySplitConditionalClassToken(raw, out var name, out var condition))
        {
            return IsSafeClassToken(raw);
        }

        if (!IsSafeClassToken(name) || !IsWhenExpression(condition))
        {
            return false;
        }

        token = name + ConditionalClassSeparator + NormalizeWhen(condition);
        return true;
    }

    private static bool IsSafeClassToken(string token)
    {
        if (token.Length == 0 || token.Length > 64)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 递归净化结构：丢弃白名单外的标签/槽位、重复槽位、超深与超量部件。
    /// 任一部件被丢弃时其子树一并丢弃，绝不做“就近塞入”的猜测。
    /// <paramref name="allowUndeclaredSlots"/> 供继承定义使用：自定义槽位可能由父定义声明，
    /// 本定义读入/写回时先受理，合并后再按父链并集统一校验。
    /// </summary>
    public static UserBlockStructureSanitizeResult Sanitize(
        UserBlockStructure? structure,
        bool allowUndeclaredSlots = false)
    {
        if (structure?.Root is null)
        {
            return UserBlockStructureSanitizeResult.Empty;
        }

        var issues = new List<UserBlockStructureIssue>();
        var slots = NormalizeSlotDeclarations(structure.Slots, issues);
        var state = new SanitizeState(issues, slots, structure.AllowTransparentParts, allowUndeclaredSlots);
        var root = SanitizePart(structure.Root, depth: 1, state);

        return root is null
            ? new UserBlockStructureSanitizeResult(UserBlockStructure.Empty, issues)
            : new UserBlockStructureSanitizeResult(
                new UserBlockStructure
                {
                    Root = root,
                    Slots = slots,
                    AllowTransparentParts = structure.AllowTransparentParts,
                },
                issues);
    }

    private sealed class SanitizeState(
        List<UserBlockStructureIssue> issues,
        IReadOnlyList<string> customSlots,
        bool allowTransparent,
        bool allowUndeclaredSlots)
    {
        public List<UserBlockStructureIssue> Issues { get; } = issues;

        public IReadOnlyList<string> CustomSlots { get; } = customSlots;

        public bool AllowTransparent { get; } = allowTransparent;

        public bool AllowUndeclaredSlots { get; } = allowUndeclaredSlots;

        public HashSet<string> Slots { get; } = new(StringComparer.Ordinal);

        public int Count { get; set; }
    }

    private static UserBlockPart? SanitizePart(UserBlockPart part, int depth, SanitizeState state)
    {
        if (depth > MaxDepth)
        {
            state.Issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_TOO_DEEP",
                $"结构嵌套超过 {MaxDepth} 层，已丢弃更深处的部件。"));
            return null;
        }

        if (state.Count >= MaxParts)
        {
            state.Issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_TOO_MANY",
                $"结构部件数超过 {MaxParts}，已丢弃超出的部件。"));
            return null;
        }

        // 片段引用是装载期占位符：不校验标签/策略/槽位，交由组合校验统一判环、缺引用与部件碰撞。
        if (part.Include is not null)
        {
            state.Count++;
            return part;
        }

        // 安全策略先于标签解析：受控标签（a/img/input）只有在匹配策略下才被标签白名单放行。
        var policy = part.Policy;
        if (!string.IsNullOrWhiteSpace(policy))
        {
            if (!TryNormalizePolicy(policy, out var normalizedPolicy))
            {
                state.Issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_POLICY_UNKNOWN",
                    $"结构部件安全策略 '{policy}' 不在允许的枚举内，已丢弃该策略。"));
                policy = null;
            }
            else
            {
                policy = normalizedPolicy;
            }
        }
        else
        {
            policy = null;
        }

        if (IsForbiddenTag(part.Tag))
        {
            state.Issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_POLICY_TAG_FORBIDDEN",
                $"结构标签 '{part.Tag}' 被安全策略禁止，已丢弃该部件及其子部件。"));
            return null;
        }

        var transparent = string.IsNullOrWhiteSpace(part.Tag) && state.AllowTransparent;
        string? tag = null;
        if (!transparent)
        {
            if (!TryNormalizeTag(part.Tag, policy, out var normalizedTag))
            {
                state.Issues.Add(new UserBlockStructureIssue(
                    IsControlledTag(part.Tag)
                        ? "USER_ELEMENTS_STRUCTURE_POLICY_TAG_MISMATCH"
                        : "USER_ELEMENTS_STRUCTURE_TAG_INVALID",
                    IsControlledTag(part.Tag)
                        ? $"受控标签 '{part.Tag}' 缺少匹配的安全策略，已丢弃该部件及其子部件。"
                        : $"结构标签 '{part.Tag}' 不在白名单内，已丢弃该部件及其子部件。"));
                return null;
            }

            tag = normalizedTag;
        }

        if (tag is not null && policy is not null && !PolicyAllowsTag(policy, tag))
        {
            state.Issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_POLICY_TAG_MISMATCH",
                $"安全策略 '{policy}' 与标签 '{tag}' 不匹配，已丢弃该策略（标签仍按通用部件处理）。"));
            policy = null;
        }

        if (!TryNormalizePart(part.Part, state.CustomSlots, state.AllowUndeclaredSlots, out var slot))
        {
            // 格式合法的未登记槽名与“格式非法”分开报码：前者是声明缺失，后者是拼写/字符面不合法。
            var undeclared = TryNormalizeSlotName(part.Part, out _);
            state.Issues.Add(new UserBlockStructureIssue(
                undeclared
                    ? "USER_ELEMENTS_STRUCTURE_SLOT_UNDECLARED"
                    : "USER_ELEMENTS_STRUCTURE_SLOT_INVALID",
                undeclared
                    ? $"结构槽位 '{part.Part}' 未在定义级 slots 中声明，已丢弃该部件及其子部件。"
                    : $"结构槽位 '{part.Part}' 未知，已丢弃该部件及其子部件。"));
            return null;
        }

        if (slot != WrapperPart && !state.Slots.Add(slot))
        {
            state.Issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_SLOT_DUPLICATE",
                $"结构槽位 '{slot}' 重复声明，已保留首次出现的部件并丢弃重复项。"));
            return null;
        }

        state.Count++;

        var rejected = new List<string>();
        var sanitizedClass = SanitizeClass(part.Class, rejected);
        if (rejected.Count > 0)
        {
            state.Issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_CLASS_INVALID",
                $"类名 '{string.Join(' ', rejected)}' 含非法字符，已丢弃该 token。"));
        }

        var rejectedStyle = new List<string>();
        var sanitizedStyle = UserBlockStyleRules.Normalize(part.Style, rejectedStyle);
        if (rejectedStyle.Count > 0)
        {
            state.Issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_STYLE_INVALID",
                $"布局声明 '{string.Join('；', rejectedStyle)}' 不在白名单内，已丢弃该声明。"));
        }

        var when = part.When;
        if (!string.IsNullOrWhiteSpace(when))
        {
            var normalizedWhen = NormalizeWhen(when);
            if (normalizedWhen is null)
            {
                state.Issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_WHEN_TEMPLATE_INVALID",
                    $"结构部件 '{slot}' 的条件 '{when}' 非法，已丢弃该条件（部件仍按无条件处理）。"));
            }

            when = normalizedWhen;
        }
        else
        {
            when = null;
        }

        // 源数据条件：非法值丢弃（部件仍按无条件发射）；与字段条件并存时丢弃源数据条件，
        // 因为一个部件只有一个条件槽位，叠加会变成隐式 AND。
        var whenSource = part.WhenSource;
        if (!string.IsNullOrWhiteSpace(whenSource))
        {
            if (!TryNormalizeWhenSource(whenSource, out var normalizedWhenSource))
            {
                state.Issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_WHEN_SOURCE_UNKNOWN",
                    $"结构部件 '{slot}' 的源数据条件 '{whenSource}' 不在允许的枚举内，已丢弃该条件（部件仍按无条件处理）。"));
                whenSource = null;
            }
            else if (when is not null)
            {
                state.Issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_WHEN_SOURCE_CONFLICT",
                    $"结构部件 '{slot}' 同时声明了字段条件与源数据条件，已丢弃源数据条件（只保留一个条件槽位）。"));
                whenSource = null;
            }
            else
            {
                whenSource = normalizedWhenSource;
            }
        }
        else
        {
            whenSource = null;
        }

        // 重复集合非法时丢弃重复声明；上限非法时回落到缺省上限（仍有界）。
        // 保存路径另由 ValidateRepeatFields 整条阻断，读取路径保留可渲染的最小安全结构。
        var repeat = part.Repeat;
        var repeatMax = part.RepeatMax;
        if (!string.IsNullOrWhiteSpace(repeat))
        {
            if (!TryNormalizeRepeat(repeat, out var normalizedRepeat))
            {
                state.Issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_REPEAT_COLLECTION_UNKNOWN",
                    $"结构部件 '{slot}' 的重复集合 '{repeat}' 不在允许的派生集合内，已丢弃该重复声明。"));
                repeat = null;
                repeatMax = DefaultRepeatMax;
            }
            else
            {
                repeat = normalizedRepeat;
                if (!IsValidRepeatMax(repeatMax))
                {
                    state.Issues.Add(new UserBlockStructureIssue(
                        "USER_ELEMENTS_STRUCTURE_REPEAT_MAX_INVALID",
                        $"结构部件 '{slot}' 的重复上限 {repeatMax} 越界，已回落到 {DefaultRepeatMax}。"));
                    repeatMax = DefaultRepeatMax;
                }
            }
        }
        else
        {
            repeat = null;
            repeatMax = DefaultRepeatMax;
        }

        // 生成器名必须在固定注册表内；名字非法即丢弃生成器与参数，
        // 参数非法只丢参数（生成器本身仍可用，保持最小安全结构）。
        var generator = part.Generator;
        var generatorParams = part.GeneratorParams;
        if (!string.IsNullOrWhiteSpace(generator))
        {
            if (!TryNormalizeGenerator(generator, out var normalizedGenerator))
            {
                state.Issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_GENERATOR_UNKNOWN",
                    $"结构部件 '{slot}' 的生成器 '{generator}' 不在允许的注册表内，已丢弃该生成器声明。"));
                generator = null;
                generatorParams = null;
            }
            else
            {
                generator = normalizedGenerator;
                if (!TryNormalizeGeneratorParams(
                        generator,
                        generatorParams,
                        out var normalizedParams,
                        out var errorCode))
                {
                    state.Issues.Add(new UserBlockStructureIssue(
                        errorCode!,
                        $"结构部件 '{slot}' 的生成器参数 '{generatorParams}' 非法，已丢弃参数。"));
                    generatorParams = null;
                }
                else
                {
                    generatorParams = normalizedParams;
                }
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(generatorParams))
            {
                state.Issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_GENERATOR_PARAMS_WITHOUT_GENERATOR",
                    $"结构部件 '{slot}' 的生成器参数缺少生成器声明，已丢弃。"));
            }

            generator = null;
            generatorParams = null;
        }

        // 引用索引值只能是固定三个派生值，且引用部件必须是叶子（引用值替换内容，不承载子结构）。
        var reference = part.Ref;
        if (!string.IsNullOrWhiteSpace(reference))
        {
            if (!TryNormalizeRef(reference, out var normalizedRef))
            {
                state.Issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_REF_UNKNOWN",
                    $"结构部件 '{slot}' 的引用值 '{reference}' 不在允许的枚举内，已丢弃该引用。"));
                reference = null;
            }
            else if (part.Children.Count > 0)
            {
                state.Issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_REF_REQUIRES_LEAF",
                    $"结构部件 '{slot}' 声明了引用 {normalizedRef} 但仍含子部件，已丢弃该引用。"));
                reference = null;
            }
            else
            {
                reference = normalizedRef;
            }
        }
        else
        {
            reference = null;
        }

        // 原文槽与引用/重复/生成器互斥：它们都在替换同一份内容，叠加只会产生“谁生效”的隐式语义。
        // 原文槽必须是叶子；内容由渲染器转义写入，透传标签只能走 policy。
        var raw = part.Raw;
        if (raw)
        {
            if (reference is not null || repeat is not null || generator is not null)
            {
                state.Issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_RAW_CONFLICT",
                    $"结构部件 '{slot}' 同时声明了原文槽与引用/重复/生成器，已丢弃原文槽（内容源互斥）。"));
                raw = false;
            }
            else if (part.Children.Count > 0)
            {
                state.Issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_RAW_REQUIRES_LEAF",
                    $"结构部件 '{slot}' 声明了原文槽但仍含子部件，已丢弃原文槽（原文替换内容，不承载子结构）。"));
                raw = false;
            }
        }

        // 槽位文本：必须是 {field} 单洞模板，取已声明字段的值并转义写入；
        // 与 raw/ref/repeat/generator 同属内容源，互斥且必须为叶子。
        var text = part.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            text = null;
        }
        else if (!TryParseFieldTemplate(text, out _))
        {
            state.Issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_TEXT_TEMPLATE_INVALID",
                $"结构部件 '{slot}' 的槽位文本 '{text}' 不是 '{{field}}' 形式，已丢弃。"));
            text = null;
        }
        else if (raw || reference is not null || repeat is not null || generator is not null)
        {
            state.Issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_TEXT_CONFLICT",
                $"结构部件 '{slot}' 同时声明了槽位文本与原文槽/引用/重复/生成器，已丢弃槽位文本（内容源互斥）。"));
            text = null;
        }
        else if (part.Children.Count > 0)
        {
            state.Issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_TEXT_REQUIRES_LEAF",
                $"结构部件 '{slot}' 声明了槽位文本但仍含子部件，已丢弃槽位文本（字段值替换内容，不承载子结构）。"));
            text = null;
        }

        // 受控 URL 绑定：只开 a→href、img→src 两个固定组合，值必须是纯洞 {field}。
        // 声明非法时丢弃该绑定（部件本身保留，标签/内容不受影响）；保存路径另由
        // ValidateBindingFields 整条阻断，读取路径保留可渲染的最小安全结构。
        var href = tag is null ? null : SanitizeBound(part, HrefAttribute, part.Href, tag, state.Issues);
        var src = tag is null ? null : SanitizeBound(part, SrcAttribute, part.Src, tag, state.Issues);
        if (src is not null && part.Children.Count > 0)
        {
            state.Issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_BIND_REQUIRES_LEAF",
                $"结构部件 '{slot}' 声明了图片地址绑定但仍含子部件，已丢弃该绑定（img 必须是叶子）。"));
            src = null;
        }

        // 命名内容槽/配方：取值必须命中固定注册表，未命中即丢弃该声明（部件本身保留）。
        var content = SanitizeRecipeName(part.Content, "content", "USER_ELEMENTS_STRUCTURE_CONTENT_UNKNOWN",
            TryNormalizeContentWriter, slot, state.Issues);
        var css = SanitizeRecipeName(part.Css, "css", "USER_ELEMENTS_STRUCTURE_CSS_UNKNOWN",
            TryNormalizeCssRecipe, slot, state.Issues);
        var classRecipe = SanitizeRecipeName(part.ClassRecipe, "class-recipe", "USER_ELEMENTS_STRUCTURE_CLASS_RECIPE_UNKNOWN",
            TryNormalizeClassRecipe, slot, state.Issues);
        var tagCondition = SanitizeRecipeName(part.TagCondition, "tag-if", "USER_ELEMENTS_STRUCTURE_TAG_CONDITION_UNKNOWN",
            TryNormalizeTagCondition, slot, state.Issues);
        var role = SanitizeRecipeName(part.Role, "role", "USER_ELEMENTS_STRUCTURE_ROLE_UNKNOWN",
            TryNormalizePartRole, slot, state.Issues);
        var sourceCondition = SanitizeRecipeName(part.SourceCondition, "source-condition",
            "USER_ELEMENTS_STRUCTURE_SOURCE_CONDITION_UNKNOWN", TryNormalizeSourceCondition, slot, state.Issues);
        var childCollection = SanitizeRecipeName(part.ChildCollection, "child-collection",
            "USER_ELEMENTS_STRUCTURE_CHILD_COLLECTION_UNKNOWN", TryNormalizeCollection, slot, state.Issues);
        var emitWhenCollection = SanitizeRecipeName(part.EmitWhenCollection, "emit-when-collection",
            "USER_ELEMENTS_STRUCTURE_EMIT_WHEN_COLLECTION_UNKNOWN", TryNormalizeCollection, slot, state.Issues);
        var emptyCollection = SanitizeRecipeName(part.EmptyCollection, "empty-collection",
            "USER_ELEMENTS_STRUCTURE_EMPTY_COLLECTION_UNKNOWN", TryNormalizeEmptyCollection, slot, state.Issues);

        // 空集合占位只在有成员收集声明时生效，单独出现属于自相矛盾的结构。
        if (emptyCollection is not null && childCollection is null)
        {
            state.Issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_EMPTY_COLLECTION_ORPHAN",
                $"结构部件 '{slot}' 声明了空集合占位但没有成员收集，已丢弃该占位。"));
            emptyCollection = null;
        }

        // 命名内容槽与其它内容源互斥：一个部件只能有一个内容源。
        // repeat 只是驱动（每个重复项各写一次），与“写什么内容”正交，故不在此列。
        if (content is not null
            && (raw || reference is not null || generator is not null || text is not null))
        {
            state.Issues.Add(new UserBlockStructureIssue(
                "USER_ELEMENTS_STRUCTURE_CONTENT_CONFLICT",
                $"结构部件 '{slot}' 同时声明了命名内容槽与原文槽/引用/生成器/槽位文本，已丢弃命名内容槽（内容源互斥）。"));
            content = null;
        }

        var attributes = new List<UserBlockPartAttribute>();
        foreach (var binding in part.Attributes)
        {
            if (TryNormalizeAttributeBinding($"{binding.Source}@{binding.Name}", out var normalizedBinding))
            {
                attributes.Add(normalizedBinding);
            }
            else
            {
                state.Issues.Add(new UserBlockStructureIssue(
                    "USER_ELEMENTS_STRUCTURE_ATTRIBUTE_BINDING_INVALID",
                    $"结构部件 '{slot}' 的属性绑定 '{binding.Name}@{binding.Source}' 不在允许的取值器/属性名内，已丢弃。"));
            }
        }

        var children = new List<UserBlockPart>(part.Children.Count);
        foreach (var child in part.Children)
        {
            var sanitized = SanitizePart(child, depth + 1, state);
            if (sanitized is not null)
            {
                children.Add(sanitized);
            }
        }

        return new UserBlockPart
        {
            Part = slot,
            Tag = tag,
            Addressable = part.Addressable,
            When = when,
            WhenSource = whenSource,
            Repeat = repeat,
            RepeatMax = repeatMax,
            Generator = generator,
            GeneratorParams = generatorParams,
            Ref = reference,
            Policy = policy,
            Raw = raw,
            Text = text,
            Href = href,
            Src = src,
            Class = sanitizedClass,
            Style = sanitizedStyle,
            Content = content,
            Css = css,
            ClassRecipe = classRecipe,
            TagCondition = tagCondition,
            Role = role,
            AttributesAfterIdentity = part.AttributesAfterIdentity,
            SourceCondition = sourceCondition,
            ChildCollection = childCollection,
            EmitWhenCollection = emitWhenCollection,
            EmptyCollection = emptyCollection,
            EmitNodeIdentity = part.EmitNodeIdentity,
            SelfClosing = part.SelfClosing,
            SuppressNodeStyle = part.SuppressNodeStyle,
            EmitNodeStyle = part.EmitNodeStyle,
            EmitNone = part.EmitNone,
            Attributes = attributes,
            Children = children,
            // 声明面必须随净化一起带过去：继承合并按“源 XML 是否出现该属性”判断覆盖。
            DeclaredFields = part.DeclaredFields,
        };
    }

    private static string? SanitizeRecipeName(
        string? value,
        string attributeName,
        string errorCode,
        RecipeNormalizer normalize,
        string slot,
        List<UserBlockStructureIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (normalize(value, out var normalized))
        {
            return normalized;
        }

        issues.Add(new UserBlockStructureIssue(
            errorCode,
            $"结构部件 '{slot}' 的命名{attributeName}取值 '{value}' 不在允许的注册表内，已丢弃该声明。"));
        return null;
    }

    private static string? SanitizeBound(
        UserBlockPart part,
        string attributeName,
        string? template,
        string tag,
        List<UserBlockStructureIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        if (TryNormalizeBoundAttribute(tag, attributeName, template, out var normalized, out var errorCode))
        {
            return normalized;
        }

        issues.Add(new UserBlockStructureIssue(
            errorCode!,
            $"结构部件 '{part.Part}' 的 {attributeName} 绑定 '{template}' 非法，已丢弃该绑定。"));
        return null;
    }

    /// <summary>
    /// 结构是否使用了 schema v2 字段。v1 定义（无插值、全部可寻址）必须继续产出 v1 文本与 v1 指纹，
    /// 保证历史定义字节与缓存键不变。
    /// </summary>
    public static bool UsesSchemaV2(UserBlockStructure? structure) =>
        structure?.Root is not null
        && (structure.Slots.Count > 0 || PartUsesSchemaV2(structure.Root));

    private static bool PartUsesSchemaV2(UserBlockPart part) =>
        !part.Addressable
        || part.When is not null
        || part.WhenSource is not null
        || part.Repeat is not null
        || part.Generator is not null
        || part.Ref is not null
        || part.Policy is not null
        || part.Raw
        || part.Text is not null
        || part.Href is not null
        || part.Src is not null
        || part.Content is not null
        || part.Css is not null
        || part.ClassRecipe is not null
        || part.TagCondition is not null
        || part.Role is not null
        || part.AttributesAfterIdentity
        || part.SourceCondition is not null
        || part.ChildCollection is not null
        || part.EmitWhenCollection is not null
        || part.EmptyCollection is not null
        || !part.EmitNodeIdentity
        || part.SelfClosing
        || part.SuppressNodeStyle
        || part.EmitNodeStyle
        || part.EmitNone
        || part.Attributes.Count > 0
        || IsTagExpression(part.Tag)
        || part.Children.Any(PartUsesSchemaV2);

    /// <summary>结构里是否声明了正文槽位（body 或 content）。</summary>
    public static bool HasContentSlot(UserBlockStructure? structure) =>
        structure?.Root is not null && ContainsContentSlot(structure.Root);

    private static bool ContainsContentSlot(UserBlockPart part)
    {
        if (string.Equals(part.Part, "body", StringComparison.Ordinal)
            || string.Equals(part.Part, "content", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var child in part.Children)
        {
            if (ContainsContentSlot(child))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 结构的规范文本，用于指纹与模板缓存键。<b>子部件顺序参与</b>——顺序即 DOM 顺序，
    /// 语义不同就必须得到不同的串；类名顺序同样参与（直接决定 class 属性字节）。
    /// </summary>
    public static string Describe(UserBlockStructure? structure)
    {
        if (structure?.Root is null)
        {
            return string.Empty;
        }

        var useV2 = UsesSchemaV2(structure);
        var builder = new StringBuilder(128);
        builder.Append(useV2 ? "structure{v=2;" : "structure{v=1;");
        // 自定义槽位声明进指纹：增删/改名/改顺序都必须换缓存键；历史定义没有这一段。
        if (useV2 && structure.Slots.Count > 0)
        {
            builder.Append("slots=").Append(string.Join(',', structure.Slots)).Append(';');
        }

        AppendPart(builder, structure.Root, useV2);
        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendPart(StringBuilder builder, UserBlockPart part, bool useV2)
    {
        builder.Append("part=").Append(part.Part).Append(';');
        builder.Append("tag=").Append(part.Tag).Append(';');
        // v2 指纹显式记录模板前缀，前缀变更必须换缓存键；v1 串不出现该字段。
        if (useV2 && TryGetTagPrefix(part.Tag, out var prefix) && prefix.Length > 0)
        {
            builder.Append("prefix=").Append(prefix).Append(';');
        }

        // 只在 v2 且显式关闭时追加：v1 串与“开寻址”的 v2 串都不多一个字段。
        if (useV2 && !part.Addressable)
        {
            builder.Append("addr=0;");
        }

        // 条件参与指纹：改条件必须换缓存键；v1 串不出现该字段。
        if (useV2 && part.When is not null)
        {
            builder.Append("when=").Append(part.When).Append(';');
        }

        // 源数据条件参与指纹：换条件必须换缓存键；v1 串不出现该字段。
        if (useV2 && part.WhenSource is not null)
        {
            builder.Append("when-source=").Append(part.WhenSource).Append(';');
        }

        // 重复与上限参与指纹：改集合或改上限都必须换缓存键；v1 串不出现该字段。
        if (useV2 && part.Repeat is not null)
        {
            builder.Append("repeat=").Append(part.Repeat).Append(';');
            builder.Append("repeat-max=")
                .Append(part.RepeatMax.ToString(CultureInfo.InvariantCulture))
                .Append(';');
        }

        // 生成器与参数参与指纹：换算法或换参数都必须换缓存键；v1 串不出现该字段。
        if (useV2 && part.Generator is not null)
        {
            builder.Append("generator=").Append(part.Generator).Append(';');
            if (part.GeneratorParams is not null)
            {
                builder.Append("generator-params=").Append(part.GeneratorParams).Append(';');
            }
        }

        // 引用索引与安全策略参与指纹：换引用值或换策略都必须换缓存键。
        if (useV2 && part.Ref is not null)
        {
            builder.Append("ref=").Append(part.Ref).Append(';');
        }

        if (useV2 && part.Policy is not null)
        {
            builder.Append("policy=").Append(part.Policy).Append(';');
        }

        // 原文槽参与指纹：启用/关闭原文槽必须换缓存键；v1 串不出现该字段。
        if (useV2 && part.Raw)
        {
            builder.Append("raw=1;");
        }

        // 槽位文本参与指纹：引用的字段名与启用状态都必须换缓存键。
        if (useV2 && part.Text is not null)
        {
            builder.Append("text=").Append(part.Text).Append(';');
        }

        // 受控 URL 绑定参与指纹：换字段或从无到有都必须换缓存键。
        if (useV2 && part.Href is not null)
        {
            builder.Append("href=").Append(part.Href).Append(';');
        }

        if (useV2 && part.Src is not null)
        {
            builder.Append("src=").Append(part.Src).Append(';');
        }

        // schema v3 原语参与指纹：换内容槽/配方/标志/属性绑定都必须换缓存键。
        if (useV2 && part.Content is not null)
        {
            builder.Append("content=").Append(part.Content).Append(';');
        }

        if (useV2 && part.Css is not null)
        {
            builder.Append("css=").Append(part.Css).Append(';');
        }

        if (useV2 && part.ClassRecipe is not null)
        {
            builder.Append("class-recipe=").Append(part.ClassRecipe).Append(';');
        }

        if (useV2 && part.TagCondition is not null)
        {
            builder.Append("tag-if=").Append(part.TagCondition).Append(';');
        }

        if (useV2 && part.Role is not null)
        {
            builder.Append("role=").Append(part.Role).Append(';');
        }

        if (useV2 && part.AttributesAfterIdentity)
        {
            builder.Append("attrs-after-identity=1;");
        }

        if (useV2 && part.SourceCondition is not null)
        {
            builder.Append("source-condition=").Append(part.SourceCondition).Append(';');
        }

        if (useV2 && part.ChildCollection is not null)
        {
            builder.Append("child-collection=").Append(part.ChildCollection).Append(';');
        }

        if (useV2 && part.EmitWhenCollection is not null)
        {
            builder.Append("emit-when-collection=").Append(part.EmitWhenCollection).Append(';');
        }

        if (useV2 && part.EmptyCollection is not null)
        {
            builder.Append("empty-collection=").Append(part.EmptyCollection).Append(';');
        }

        // 透明部件参与指纹：tag=null 与 tag= 空串必须区分。
        if (useV2 && part.Tag is null)
        {
            builder.Append("tag=none;");
        }

        if (useV2 && !part.EmitNodeIdentity)
        {
            builder.Append("no-identity=1;");
        }

        if (useV2 && part.SelfClosing)
        {
            builder.Append("self-closing=1;");
        }

        if (useV2 && part.SuppressNodeStyle)
        {
            builder.Append("suppress-style=1;");
        }

        if (useV2 && part.EmitNodeStyle)
        {
            builder.Append("emit-node-style=1;");
        }

        if (useV2 && part.EmitNone)
        {
            builder.Append("emit=none;");
        }

        foreach (var binding in part.Attributes)
        {
            builder.Append("attr=").Append(binding.Name).Append('<').Append(binding.Source).Append(';');
        }

        builder.Append("class=");
        if (part.Class is null)
        {
            builder.Append("-1;");
        }
        else
        {
            builder.Append(part.Class.Length).Append(':').Append(part.Class).Append(';');
        }

        // 只有写了布局才追加 style 段：历史定义（无 style）的指纹与缓存键保持原样。
        if (part.Style is not null)
        {
            builder.Append("style=").Append(part.Style.Length).Append(':').Append(part.Style).Append(';');
        }

        builder.Append('[');
        foreach (var child in part.Children)
        {
            AppendPart(builder, child, useV2);
            builder.Append('|');
        }

        builder.Append(']');
    }
}
