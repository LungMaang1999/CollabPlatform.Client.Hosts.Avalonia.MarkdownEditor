using System;
using System.Collections.Generic;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>类型在正文中的呈现形态，用于在类型页区分“可见/结构/标记/不渲染”。</summary>
public enum ElementRenderMode
{
    /// <summary>渲染出独立的可见 HTML 元素。</summary>
    Visible,

    /// <summary>参与结构装配，自身视觉主要由子节点或外层容器决定。</summary>
    Structural,

    /// <summary>不产生独立包裹元素，只承载文本或标记语义。</summary>
    Marker,

    /// <summary>不进入正文渲染。</summary>
    Hidden
}

/// <summary>一个语法元素类型的静态元数据。</summary>
public sealed record MarkdownElementTypeInfo(
    NodeType Type,
    string Label,
    string Category,
    ElementRenderMode RenderMode,
    bool SupportsTypeStyle,
    string SampleMarkdown,
    string Description);

/// <summary>
/// 语法元素类型目录：覆盖 <see cref="NodeType"/> 全部取值，包含不渲染与纯标记类型。
/// 类型页的左栏、声明表分组与类型样例预览都以这里为唯一来源。
/// </summary>
public static class MarkdownElementTypeCatalog
{
    public const string CategoryStructure = "文档结构";
    public const string CategoryList = "列表";
    public const string CategoryBlock = "块级";
    public const string CategoryInline = "行内";
    public const string CategoryFootnote = "脚注";
    public const string CategoryExtension = "扩展块";
    public const string CategoryPoem = "古诗部件";

    public static IReadOnlyList<MarkdownElementTypeInfo> All { get; } =
    [
        new(NodeType.Document, "文档", CategoryStructure, ElementRenderMode.Structural, true,
            "# 项目说明\n\n本文介绍文档的结构。\n\n## 快速开始\n\n- 打开文档\n- 选择元素类型\n\n## 深入配置\n\n### 样式\n\n按类型调整字体与配色。\n\n### 权限\n\n按成员分配读写权限。",
            "整篇文档的根节点，其样式是全局默认基准。"),
        new(NodeType.Section, "分节", CategoryStructure, ElementRenderMode.Structural, true,
            "## 快速开始\n\n安装后打开示例文档。\n\n### 步骤\n\n1. 新建文档\n2. 选择模板\n\n### 说明\n\n章节由标题层级自动生成。",
            "按标题层级自动生成的章节容器，没有独立源码区间。"),
        new(NodeType.Heading, "标题", CategoryStructure, ElementRenderMode.Visible, true,
            "# 一级标题", "ATX 与 Setext 标题，按等级渲染为 h1–h6。"),
        new(NodeType.Paragraph, "段落", CategoryStructure, ElementRenderMode.Visible, true,
            "这是一段正文。", "普通正文段落。"),
        new(NodeType.Text, "文本", CategoryStructure, ElementRenderMode.Marker, true,
            "正文中的纯文本片段", "纯文本片段，直接输出文字，不产生独立包裹元素。"),
        new(NodeType.LineBreak, "换行", CategoryStructure, ElementRenderMode.Marker, true,
            "第一行  \n第二行", "硬换行，行尾两个空格或反斜杠产生。"),
        new(NodeType.ThematicBreak, "分割线", CategoryStructure, ElementRenderMode.Visible, true,
            "---", "分隔线。"),
        new(NodeType.YamlFrontMatter, "元数据", CategoryStructure, ElementRenderMode.Hidden, false,
            "---\ntitle: 示例\n---", "YAML 前言，只参与元数据解析，不进入正文渲染。"),

        new(NodeType.UnorderedList, "无序列表", CategoryList, ElementRenderMode.Visible, true,
            "- 条目一\n- 条目二", "无序列表容器。"),
        new(NodeType.OrderedList, "有序列表", CategoryList, ElementRenderMode.Visible, true,
            "1. 条目一\n2. 条目二", "有序列表容器。"),
        new(NodeType.ListItem, "列表条目", CategoryList, ElementRenderMode.Visible, true,
            "- 列表条目", "列表中的单个条目。"),
        new(NodeType.TaskListItem, "任务项", CategoryList, ElementRenderMode.Visible, true,
            "- [ ] 未完成\n- [x] 已完成", "带勾选框的任务条目，checked 由源码方括号派生。"),

        new(NodeType.Quote, "引用", CategoryBlock, ElementRenderMode.Visible, true,
            "> 引用内容", "引用块。"),
        new(NodeType.CodeBlock, "代码块", CategoryBlock, ElementRenderMode.Visible, true,
            "```csharp\nvar value = 1;\n```", "围栏代码块，info 首 token 决定语言。"),
        new(NodeType.Table, "表格", CategoryBlock, ElementRenderMode.Visible, true,
            "| 列 A | 列 B |\n| --- | --- |\n| 1 | 2 |", "表格容器。"),
        new(NodeType.TableRow, "表格行", CategoryBlock, ElementRenderMode.Structural, true,
            "| 列 A | 列 B |\n| --- | --- |\n| 1 | 2 |", "表格行，样式一般由表格或单元格承载。"),
        new(NodeType.TableCell, "单元格", CategoryBlock, ElementRenderMode.Visible, true,
            "| 单元格 |", "表格单元格，区分表头与普通单元格。"),
        new(NodeType.MathBlock, "公式块", CategoryBlock, ElementRenderMode.Visible, true,
            "$$\nx^2 + y^2\n$$", "块级数学公式。"),
        new(NodeType.HtmlBlock, "HTML 块", CategoryBlock, ElementRenderMode.Visible, true,
            "<div class=\"note\">HTML 块</div>", "原始 HTML 块。"),
        new(NodeType.Custom, "自定义块", CategoryBlock, ElementRenderMode.Visible, true,
            "> info: 信息\n> kind 决定配色与图标，冒号后面的文字就是正文。", "带类型标识的自定义引用块，kind 与图标由属性控制。"),

        new(NodeType.Link, "链接", CategoryInline, ElementRenderMode.Visible, true,
            "[链接文字](https://example.com)", "超链接。"),
        // 内联 SVG 占位图：不依赖任何外部主机，任何环境都能画出图；
        // 用百分号编码而不是 base64，样例源码里不会出现一长串不可读的 base64。
        new(NodeType.Image, "图片", CategoryInline, ElementRenderMode.Visible, true,
            "![替代文字](data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='320'%20height='180'%3E%3Crect%20width='320'%20height='180'%20fill='%23e8eef7'/%3E%3Ccircle%20cx='252'%20cy='52'%20r='22'%20fill='%23f7c948'/%3E%3Cpath%20d='M0%20180L92%2088l60%2060%2042-38%20126%2070z'%20fill='%23bfd4ec'/%3E%3C/svg%3E)", "图片。"),
        new(NodeType.Strong, "加粗", CategoryInline, ElementRenderMode.Visible, true,
            "**加粗**", "加粗强调。"),
        new(NodeType.Emphasis, "斜体", CategoryInline, ElementRenderMode.Visible, true,
            "*斜体*", "斜体强调。"),
        new(NodeType.Delete, "删除线", CategoryInline, ElementRenderMode.Visible, true,
            "~~删除线~~", "删除线。"),
        new(NodeType.Subscript, "下标", CategoryInline, ElementRenderMode.Visible, true,
            "H~2~O", "下标。"),
        new(NodeType.Superscript, "上标", CategoryInline, ElementRenderMode.Visible, true,
            "x^2^", "上标。"),
        new(NodeType.Inserted, "插入", CategoryInline, ElementRenderMode.Visible, true,
            "++插入++", "插入语义的行内标记。"),
        new(NodeType.Marked, "高亮", CategoryInline, ElementRenderMode.Visible, true,
            "==高亮==", "高亮语义的行内标记。"),
        new(NodeType.Spoiler, "剧透", CategoryInline, ElementRenderMode.Visible, true,
            "||剧透内容||", "默认遮罩、点击展开的行内内容。"),
        new(NodeType.Tocpic, "目录", CategoryBlock, ElementRenderMode.Visible, true,
            "'''tocpic\n- [文章标题](/article/ID)\n'''", "用手写文章链接与嵌套列表组织的目录块。"),
        new(NodeType.InlineCode, "行内代码", CategoryInline, ElementRenderMode.Visible, true,
            "`inline code`", "行内代码。"),
        new(NodeType.ColorChip, "颜色", CategoryInline, ElementRenderMode.Visible, true,
            "`color: #16A34A 品牌绿`", "以 color: 前缀声明的色块，其余行内代码原样渲染。"),
        new(NodeType.ColorBlock, "颜色矩阵", CategoryBlock, ElementRenderMode.Visible, true,
            "'''color\n#16A34A 品牌绿\n#0F172A\n\n#F8FAFC\n'''", "一行一色、空行分行的色卡矩阵。"),
        new(NodeType.MathInline, "行内公式", CategoryInline, ElementRenderMode.Visible, true,
            "$x^2$", "行内数学公式。"),
        new(NodeType.HtmlInline, "行内 HTML", CategoryInline, ElementRenderMode.Visible, true,
            "行内 <kbd>Ctrl</kbd> 键", "原始行内 HTML。"),
        new(NodeType.FootnoteLink, "脚注引用", CategoryInline, ElementRenderMode.Visible, true,
            "正文中的脚注引用[^1]\n\n[^1]: 脚注条目", "正文中的脚注引用。"),

        new(NodeType.FootnoteGroup, "脚注区", CategoryFootnote, ElementRenderMode.Structural, true,
            "[^1]: 脚注内容", "文末脚注分组容器。"),
        new(NodeType.Footnote, "脚注条目", CategoryFootnote, ElementRenderMode.Visible, true,
            "正文中的脚注引用[^1]\n\n[^1]: 脚注条目", "单个脚注条目。"),

        new(NodeType.Mermaid, "图表", CategoryExtension, ElementRenderMode.Visible, true,
            "```mermaid\ngraph TD\nA-->B\n```", "Mermaid 图表，主题由围栏参数控制。"),
        new(NodeType.ImageMatrix, "图组", CategoryExtension, ElementRenderMode.Visible, true,
            "'''image\nhttps://example.com/a.jpg\nhttps://example.com/b.jpg\n'''", "三撇围栏图片矩阵。"),
        new(NodeType.QrCode, "二维码", CategoryExtension, ElementRenderMode.Visible, true,
            "```qrcode\nhttps://example.com\n```", "二维码块，纠错等级与静区由围栏参数控制。"),
        new(NodeType.PoemBlock, "古诗", CategoryExtension, ElementRenderMode.Visible, true,
            "'''poem\ntitle: 静夜思\nauthor: 李白\ndynasty: 唐\nform: 五言绝句\nsource: 《全唐诗》\nnote: 月夜思乡之作。\n\n---\n床前明月光\n疑是地上霜\n举头望明月\n低头思故乡\n'''",
            "三撇围栏古诗块，内部拆出多个合成部件。"),
        new(NodeType.Timeline, "时间线", CategoryExtension, ElementRenderMode.Visible, true,
            "'''timeline\n@@ 2026 Q1 [发布]\n完成首版，打通编辑与预览。\n\n@@ 2026 Q2 [迭代]\n加入元素定义与设计面。\n'''",
            "三撇围栏时间线容器。"),
        new(NodeType.TimelineItem, "时间线节点", CategoryExtension, ElementRenderMode.Structural, true,
            "'''timeline\n@@ 2026 Q1 [发布]\n完成首版，打通编辑与预览。\n\n@@ 2026 Q2 [迭代]\n加入元素定义与设计面。\n'''",
            "时间线内的合成节点，时间与标签来自 @@ 行。"),
        new(NodeType.Radar, "雷达图", CategoryExtension, ElementRenderMode.Visible, true,
            "'''radar title=产品对比\n| 维度 | A |\n| x | 1 |\n'''", "三撇围栏雷达图，表格首列是维度、其余列是数据系列。"),
        new(NodeType.Collapse, "折叠块", CategoryExtension, ElementRenderMode.Visible, true,
            "'''collapse 标题\n正文\n'''", "三撇围栏折叠块，正文按 Markdown 解析，可逐层减少引号数嵌套。"),

        new(NodeType.PoemTitle, "诗题", CategoryPoem, ElementRenderMode.Marker, true,
            "'''poem\ntitle: 静夜思\n---\n床前明月光\n'''", "古诗块内的合成部件，仅承载自身局部样式。"),
        new(NodeType.PoemAuthor, "诗作者", CategoryPoem, ElementRenderMode.Marker, true,
            "'''poem\nauthor: 李白\n---\n床前明月光\n'''", "古诗块内的合成部件，仅承载自身局部样式。"),
        new(NodeType.PoemDynasty, "诗朝代", CategoryPoem, ElementRenderMode.Marker, true,
            "'''poem\ndynasty: 唐\n---\n床前明月光\n'''", "古诗块内的合成部件，仅承载自身局部样式。"),
        new(NodeType.PoemForm, "诗体裁", CategoryPoem, ElementRenderMode.Marker, true,
            "'''poem\nform: 五言绝句\n---\n床前明月光\n'''", "古诗块内的合成部件，仅承载自身局部样式。"),
        new(NodeType.PoemStanza, "诗节", CategoryPoem, ElementRenderMode.Marker, true,
            "'''poem\n---\n床前明月光\n疑是地上霜\n'''", "古诗块内的诗节分组，仅承载自身局部样式。"),
        new(NodeType.PoemLine, "诗句", CategoryPoem, ElementRenderMode.Marker, true,
            "'''poem\n---\n床前明月光\n'''", "古诗块内的单行诗句，仅承载自身局部样式。"),
        new(NodeType.PoemSource, "诗出处", CategoryPoem, ElementRenderMode.Marker, true,
            "'''poem\nsource: 《全唐诗》\n---\n床前明月光\n'''", "古诗块内的合成部件，仅承载自身局部样式。"),
        new(NodeType.PoemNote, "诗注释", CategoryPoem, ElementRenderMode.Marker, true,
            "'''poem\nnote: 思乡之作。\n---\n床前明月光\n'''", "古诗块内的合成部件，仅承载自身局部样式。"),
    ];

    private static readonly Dictionary<NodeType, MarkdownElementTypeInfo> ByType = CreateIndex();

    public static IReadOnlyDictionary<NodeType, MarkdownElementTypeInfo> Types => ByType;

    public static MarkdownElementTypeInfo? Find(NodeType type) =>
        ByType.TryGetValue(type, out var info) ? info : null;

    public static bool TryGet(NodeType type, out MarkdownElementTypeInfo info) =>
        ByType.TryGetValue(type, out info!);

    private static Dictionary<NodeType, MarkdownElementTypeInfo> CreateIndex()
    {
        var index = new Dictionary<NodeType, MarkdownElementTypeInfo>(All.Count);
        foreach (var info in All)
        {
            if (!index.TryAdd(info.Type, info))
            {
                throw new InvalidOperationException($"类型目录存在重复项：{info.Type}。");
            }
        }

        return index;
    }
}
