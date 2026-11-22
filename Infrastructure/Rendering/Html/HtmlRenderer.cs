using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Exporting.Html;
using ExportingCssStyleBuilder = CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Exporting.Html.CssStyleBuilder;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;

internal sealed class HtmlRenderer : IDocumentRenderer, IIncrementalPreviewRenderer
{
    private static readonly HashSet<string> AllowedUriSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto", "tel"
    };

    private static readonly Regex UnsafeUriCharRegex = new(@"[\u0000-\u001F\u007F\s]", RegexOptions.Compiled);

    private static readonly char[] LanguageSeparators = { ' ', '\t' };

    private readonly IStyleResolver _styleResolver;
    private readonly ICssStyleBuilder _cssStyleBuilder;
    private readonly AdmonitionIconOptions _iconOptions;
    private readonly NodeStyle? _defaultStyle;

    // 模板注册表从第一天起按 (NodeType, Variant) 建键，避免后续从单变体回改。
    private readonly Dictionary<(NodeType NodeType, ElementTemplateVariant Variant), ElementTemplate> _templates = new();

    // 属性驱动的结构变体：只有显式声明选择器的类型才会命中，未声明的类型完全不受影响。
    // 键 = 类型 + 固定选择键；值 = “缺少该属性时是否回落到按请求变体的模板”。
    private readonly Dictionary<NodeType, TemplateSelectorBinding> _templateSelectors = new();

    // 选择器命中的结构；键 = 类型 + 匹配结果（仅存在/缺失两种）+ 变体。
    // 变体只改外形：同一匹配下缺某个变体时沿用 Default 变体的结构（与历史单模板行为一致）。
    private readonly Dictionary<(NodeType NodeType, TemplateSelectorMatch Match, ElementTemplateVariant Variant), ElementTemplate> _selectedTemplates = new();

    // 命名生成器注册表：渲染路径只按名字取算法，不出现按 NodeType 的分派。
    private readonly Dictionary<string, PartGenerator> _generators = new(StringComparer.Ordinal);

    private sealed record TemplateSelectorBinding(
        TemplateSelectorKey Key,
        TemplateSelectorMatch FallbackMatch);

    // 自定义元素的结构模板缓存：键为 (kind, 变体, 结构规范串)。
    // 结构规范串是纯内容键，因此“定义换了个实例但内容相同”不会重复构建。
    private readonly Dictionary<(string Kind, ElementTemplateVariant Variant, string Structure), ElementTemplate> _structureTemplates = new();

    // 渲染期间挂载当前文档的样式表与用户元素定义，供变体解析与结构渲染使用；渲染是同步的，结束后恢复。
    private StyleSheet? _activeStyleSheet;
    private TypeDefinitionIndex? _activeTypeDefinitions;
    private IReadOnlyDictionary<string, UserBlockDefinition>? _activeUserBlocks;

    // kind → 结构（声明或结构图）是否使用 ref；随文档状态重建，避免每个块重复解析结构图常量。
    private Dictionary<string, bool>? _structureUsesRef;
    private MarkdownDocument? _activeDocument;
    private IconConfig _activeIcons = IconConfig.Default;
    private RenderFragmentOptions _fragmentOptions = RenderFragmentOptions.None;

    /// <summary>
    /// 引用索引：整份文档渲染期间建立一次，键为节点 Id。范围是“本次渲染的整篇文档”，
    /// 渲染结束即丢弃；下一次渲染（含单块渲染）重建，因此文档树或定义变化自然失效。
    /// 索引只读节点已生效的属性与 kind，不写回节点。
    /// </summary>
    private DocumentRefIndex? _documentRefIndex;


    /// <summary>结构覆盖只作用于单块渲染的目标节点，子节点仍按各自模板渲染。</summary>
    private MarkdownNode? _structureOverrideNode;
    private ElementDefinitionInputStamp? _definitionStamp;
    private ElementDefinitionFingerprintSet? _definitionSet;
    private MarkdownNode? _kindScopeWitness;

    public HtmlRenderer(
        IStyleResolver styleResolver,
        ICssStyleBuilder? cssStyleBuilder = null,
        AdmonitionIconOptions? iconOptions = null,
        NodeStyle? defaultStyle = null)
    {
        _styleResolver = styleResolver ?? throw new ArgumentNullException(nameof(styleResolver));
        _cssStyleBuilder = cssStyleBuilder ?? new ExportingCssStyleBuilder();
        _iconOptions = iconOptions ?? new AdmonitionIconOptions();
        _defaultStyle = defaultStyle;

        RegisterGenerators();
        RegisterTemplates();
    }

    /// <summary>
    /// 生成算法注册表。键固定为 <c>UserBlockStructure</c> 中的生成器名；
    /// 算法只写内容与生成属性，不接管标签/class/节点身份。
    /// </summary>
    private void RegisterGenerators()
    {
        _generators["math"] = WriteMathGenerator;
        _generators["mermaid"] = WriteMermaidGenerator;
        _generators["image-matrix"] = WriteImageMatrixGenerator;
        _generators["qrcode"] = WriteQrCodeGenerator;
        _generators["radar"] = WriteRadarGenerator;
        _generators["code-highlight"] = WriteCodeHighlightGenerator;
    }

    public RenderResult Render(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var icons = _iconOptions.Snapshot();
        var definitions = GetDefinitions(document);
        var hasKindScopedElements = HasKindScopedElements(document.Root);
        var ctx = RenderCore(document, assembleHtml: true, icons, definitions, hasKindScopedElements);

        // 任何全量渲染都重建增量基线：补丁必须以一份完整 DOM 为起点。
        lock (_blockCacheGate)
        {
            _baseline = new IncrementalBaseline
            {
                Epoch = ctx.Epoch,
                OrderedIds = ctx.OrderedIds
            };
            _forceFullNext = false;
        }

        LastBlockCacheStats = ctx.Counters;
        return new RenderResult(ctx.Html!.ToString(), ctx.Diagnostics);
    }

    public IncrementalRenderResult RenderIncremental(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // 一次渲染只取一次图标快照：纪元指纹与后续字形都从它读，避免不同源。
        var icons = _iconOptions.Snapshot();
        var definitions = GetDefinitions(document);
        var hasKindScopedElements = HasKindScopedElements(document.Root);
        var epoch = CreateEpoch(definitions, icons, hasKindScopedElements);

        IncrementalBaseline? baseline;
        lock (_blockCacheGate)
        {
            baseline = _baseline;
            if (_forceFullNext || baseline is null || !baseline.Epoch.Equals(epoch))
            {
                baseline = null;
            }
        }

        // 无基线 / 文档级定义或图标配置变化 / 显式要求：输出全量并重建基线。
        // 类型/命名样式等局部定义不进纪元，由各自的块签名决定是否失效。
        if (baseline is null)
        {
            var full = Render(document);
            LastIncrementalStats = new IncrementalCounters(0, 0, 0, 0, FullFallback: true);
            return IncrementalRenderResult.Full(full.Html, full.Diagnostics);
        }

        var ctx = RenderCore(document, assembleHtml: false, icons, definitions, hasKindScopedElements);
        var prefixCount = _prefixCursor;
        var reuseFrame = _reuseFrame;
        LastBlockCacheStats = ctx.Counters;

        // 类型级帧策略：本帧变更块只要包含 ForceFull 类型，就放弃补丁帧改走全量。
        if (HasForceFullChange(document, ctx))
        {
            var forced = Render(document);
            LastIncrementalStats = new IncrementalCounters(
                ctx.Counters.MemoHits, ctx.Counters.MemoMisses, 0, 0, FullFallback: true);
            return IncrementalRenderResult.Full(forced.Html, forced.Diagnostics);
        }

        // 出现无法按 id 寻址的块（空/非元素片段）或补丁性价比不足时回退全量。
        if (!ctx.SawNonCacheableBlock)
        {
            if (!TryBuildSuffixPatch(baseline, ctx, prefixCount, out var ops, out var payloadChars))
            {
                var blocks = MaterializeFrameBlocks(reuseFrame, prefixCount, ctx.FrameBlocks);
                ops = BuildPatchOps(baseline, ctx.OrderedIds, blocks, out payloadChars);
            }

            var fullLengthEstimate = PrefixFragmentChars(reuseFrame, prefixCount);
            foreach (var fb in ctx.FrameBlocks)
            {
                fullLengthEstimate += fb.Fragment.Length;
            }

            var tooManyOps = ops.Count > Math.Max(1, ctx.OrderedIds.Count) * PatchMaxOpRatio;
            var tooLargePayload =
                payloadChars > (long)Math.Max(16, fullLengthEstimate) * PatchMaxPayloadRatio;

            if (!tooManyOps && !tooLargePayload)
            {
                lock (_blockCacheGate)
                {
                    _baseline = new IncrementalBaseline
                    {
                        Epoch = ctx.Epoch,
                        OrderedIds = ctx.OrderedIds
                    };
                }

                var patch = new PreviewPatch(ops);
                LastIncrementalStats = new IncrementalCounters(
                    ctx.Counters.MemoHits, ctx.Counters.MemoMisses, ops.Count, payloadChars, false);
                return IncrementalRenderResult.Incremental(patch, ctx.Diagnostics);
            }
        }

        var fallback = Render(document);
        LastIncrementalStats = new IncrementalCounters(
            ctx.Counters.MemoHits, ctx.Counters.MemoMisses, 0, 0, FullFallback: true);
        return IncrementalRenderResult.Full(fallback.Html, fallback.Diagnostics);
    }

    public void ResetIncrementalBaseline()
    {
        lock (_blockCacheGate)
        {
            _baseline = null;
            _prefixFrame = null;
            _kindScopeWitness = null;
            _forceFullNext = true;
        }
    }

    /// <summary>
    /// 判断本帧的变更块是否命中 ForceFull 类型：命中时补丁帧不足以安全表达
    /// 该类型的变化，调用方应输出全量帧。
    /// </summary>
    private static bool HasForceFullChange(MarkdownDocument document, RenderContext ctx)
    {
        var forceFullTypes = CollectForceFullTypes(document);
        if (forceFullTypes.Count == 0) return false;

        for (var i = 0; i < ctx.FrameBlocks.Count; i++)
        {
            var block = ctx.FrameBlocks[i];
            if (block.SignatureChanged && ContainsType(block.Node, forceFullTypes))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<NodeType> CollectForceFullTypes(MarkdownDocument document)
    {
        var result = new HashSet<NodeType>();
        var styles = document.StyleSheet.Styles;
        for (var i = 0; i < styles.Count; i++)
        {
            var definition = styles[i];
            // kind 限定的 ForceFull 定义只作用于该 kind，但按类型放弃补丁帧是安全侧近似：
            // 宁可多一次全量，也不让需要整篇替换的变体走补丁。
            if (definition.AppliesTo is { } type
                && definition.Behavior.FrameStrategy == ElementFrameStrategy.ForceFull)
            {
                result.Add(type);
            }
        }

        return result;
    }

    private static bool ContainsType(MarkdownNode node, HashSet<NodeType> types)
    {
        if (types.Contains(node.Type)) return true;
        for (var i = 0; i < node.Children.Count; i++)
        {
            if (ContainsType(node.Children[i], types)) return true;
        }

        return false;
    }

    /// <summary>最近一次 Render 的块缓存命中情况（供性能验证/基准测试读取）。</summary>
    internal BlockCounters LastBlockCacheStats { get; private set; }

    /// <summary>最近一次 RenderIncremental 的补丁统计（供基准测试读取）。</summary>
    internal IncrementalCounters LastIncrementalStats { get; private set; }

    internal readonly record struct BlockCounters(int Total, int FromCache, int MemoHits, int MemoMisses)
    {
        public double HitRate => Total == 0 ? 0 : (double)FromCache / Total;
    }

    internal readonly record struct IncrementalCounters(
        int MemoHits, int MemoMisses, int Ops, long PayloadChars, bool FullFallback);

    private const double PatchMaxOpRatio = 0.5;
    private const double PatchMaxPayloadRatio = 0.5;
    private const int EstimatedOpOverhead = 32;

    // 缓存纪元只由“对整篇都生效的输入”决定：文档级定义指纹 + 图标配置内容指纹。
    // 类型/命名样式等局部定义不再进纪元，而是折叠进各自块的签名，实现“改一处只失效相关块”。
    // 两者都用内容指纹而非递增计数器，重复下发同一配置（顺序/次数不同）不会改变签名。
    private readonly record struct CacheEpoch(string DocumentDefinitionFingerprint, string IconFingerprint)
    {
        public string Prefix { get; } =
            string.Concat(DocumentDefinitionFingerprint, "|", IconFingerprint, "|");
    }

    /// <summary>
    /// 构造缓存纪元。图标分两种作用域：
    /// <list type="bullet">
    /// <item>文档里没有“按 kind 寻址”的自定义元素时（含全部内置类型与内置自定义内容块），
    /// 整份图标指纹进纪元——与历史行为逐字节一致，基线不受影响；</item>
    /// <item>存在按 kind 寻址的自定义元素时，图标配置改为逐节点折叠（见
    /// <see cref="FoldNodeIcons"/>），纪元不再含图标指纹。否则改一个 kind 的图标
    /// 会让整篇失效，达不到“只失效含该 kind 的块”。</item>
    /// </list>
    /// </summary>
    private static CacheEpoch CreateEpoch(
        ElementDefinitionFingerprintSet definitions,
        IconConfig icons,
        bool hasKindScopedElements) =>
        new(definitions.DocumentLevel, hasKindScopedElements ? string.Empty : icons.Fingerprint);

    /// <summary>
    /// 判断文档是否需要“kind 级图标作用域”：存在 custom-kind 不属于内置自定义内容块类别的
    /// 自定义元素。内置类别（info/success/...）沿用旧的全局图标纪元，保证既有输出与基线不变。
    /// </summary>
    private ElementDefinitionFingerprintSet GetDefinitions(MarkdownDocument document)
    {
        var sheet = document.StyleSheet;
        var blocks = document.EffectiveUserBlockDefinitions;
        var stages = document.GraphStages;
        // 缺省样式是指纹输入的一部分；必须先补齐，否则命中判断看到的集合和 ComputeSet 不一致。
        sheet.GetOrCreateDefault();
        if (_definitionSet is not null
            && _definitionStamp is not null
            && _definitionStamp.Matches(sheet, blocks, stages))
        {
            return _definitionSet;
        }

        var set = ElementDefinitionFingerprint.ComputeSet(sheet, blocks, stages);
        _definitionStamp = ElementDefinitionInputStamp.Capture(sheet, blocks, stages);
        _definitionSet = set;
        return set;
    }

    /// <summary>
    /// 判断文档是否需要“kind 级图标作用域”。上一帧的 witness 只有在仍挂在当前根的子节点链上，
    /// 且 kind 仍是非内置自定义 kind 时才能短路；上一帧为 false 不能跳过，流式追加会加节点。
    /// 直接 <c>Children.Remove</c> 不会清 Parent，所以父链之外还要核对每一跳仍在 Children 里。
    /// </summary>
    private bool HasKindScopedElements(MarkdownNode root)
    {
        var witness = _kindScopeWitness;
        if (witness is not null && IsKindScoped(witness) && IsAttached(witness, root))
        {
            return true;
        }

        witness = FindKindScopeWitness(root);
        _kindScopeWitness = witness;
        return witness is not null;
    }

    private static bool IsKindScoped(MarkdownNode node)
    {
        if (node.Type != NodeType.Custom)
        {
            return false;
        }

        var kind = ElementKind.Resolve(node);
        return kind is not null && !AdmonitionCatalog.CustomBlockKinds.Contains(kind);
    }

    private static bool IsAttached(MarkdownNode node, MarkdownNode root)
    {
        const int maxParentHops = 1024;
        var hops = 0;
        for (var current = node; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }

            var parent = current.Parent;
            if (parent is null || !ContainsChild(parent, current) || ++hops > maxParentHops)
            {
                return false;
            }
        }

        return false;
    }

    private static bool ContainsChild(MarkdownNode parent, MarkdownNode child)
    {
        var children = parent.Children;
        for (var i = 0; i < children.Count; i++)
        {
            if (ReferenceEquals(children[i], child))
            {
                return true;
            }
        }

        return false;
    }

    private static MarkdownNode? FindKindScopeWitness(MarkdownNode node)
    {
        if (IsKindScoped(node))
        {
            return node;
        }

        for (var i = 0; i < node.Children.Count; i++)
        {
            var found = FindKindScopeWitness(node.Children[i]);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private sealed record BlockEntry(
        string Signature,
        string Html,
        MarkdownNode Node,
        CacheEpoch Epoch,
        string DefinitionStamp,
        long StyleRevision,
        string IconStamp);

    private sealed class IncrementalBaseline
    {
        public required CacheEpoch Epoch { get; init; }
        public required List<string> OrderedIds { get; init; }
    }

    private sealed class FrameBlock
    {
        public required string Id { get; init; }
        public required string Fragment { get; init; }
        public required string Signature { get; init; }
        public required MarkdownNode Node { get; init; }
        public string? PreviousSignature { get; init; }
        public bool SignatureChanged =>
            PreviousSignature is null || !string.Equals(PreviousSignature, Signature, StringComparison.Ordinal);
    }

    private sealed class RenderContext
    {
        public required CacheEpoch Epoch { get; init; }
        public required ElementDefinitionFingerprintSet Definitions { get; init; }
        public required IconConfig Icons { get; init; }

        /// <summary>true 时图标不进纪元，改由每个块折叠自己用到的字形。</summary>
        public required bool ScopeIconsPerBlock { get; init; }
        public required long StyleRevision { get; init; }
        public required List<DiagnosticMessage> Diagnostics { get; init; }
        public required bool AssembleHtml { get; init; }
        public StringBuilder? Html { get; set; }
        public HashSet<string> LiveIds { get; } = new(StringComparer.Ordinal);
        public List<string> OrderedIds { get; } = new();
        public List<FrameBlock> FrameBlocks { get; } = new();
        public BlockCounters Counters { get; set; }
        public bool SawNonCacheableBlock { get; set; }
    }

    private readonly object _blockCacheGate = new();
    private readonly Dictionary<string, BlockEntry> _blockCache = new(StringComparer.Ordinal);
    private IncrementalBaseline? _baseline;
    private PrefixFrame? _prefixFrame;
    private PrefixFrame? _reuseFrame;
    private int _prefixCursor;
    private bool _prefixOpen;
    private bool _forceFullNext;

    private RenderContext RenderCore(
        MarkdownDocument document,
        bool assembleHtml,
        IconConfig icons,
        ElementDefinitionFingerprintSet definitions,
        bool hasKindScopedElements)
    {
        var ctx = CreateContext(document, assembleHtml, icons, definitions, hasKindScopedElements);
        BeginPrefixReuse(document, ctx);

        // 模板变体与图标快照都来自当前文档/当前配置；渲染同步执行，结束后恢复外层状态。
        ExecuteWithDocumentState(document, icons, () =>
        {
            ctx.Html?.AppendLine("<article class=\"markdown-document\">");
            RenderBlockBoundaries(document.Root, ctx);
            ctx.Html?.AppendLine("</article>");
        });

        SweepStaleBlockFragments(ctx.LiveIds);
        PublishPrefixFrame(document, ctx);
        return ctx;
    }

    /// <summary>
    /// 挂载“当前文档的样式表 / 类型定义 / 用户元素定义 / 图标快照”，执行完恢复。
    /// 整篇渲染、单块渲染与模板描述都走这一处，避免各自挂一半状态。
    /// </summary>
    private void ExecuteWithDocumentState(MarkdownDocument document, IconConfig icons, Action action)
    {
        var previousSheet = _activeStyleSheet;
        var previousDefinitions = _activeTypeDefinitions;
        var previousUserBlocks = _activeUserBlocks;
        var previousDocument = _activeDocument;
        var previousIcons = _activeIcons;
        var previousRefIndex = _documentRefIndex;
        var previousStructureUsesRef = _structureUsesRef;
        _activeStyleSheet = document.StyleSheet;
        _activeTypeDefinitions = null;
        _activeUserBlocks = BuildUserBlockIndex(document.EffectiveUserBlockDefinitions);
        _activeDocument = document;
        _activeIcons = icons;
        _documentRefIndex = null;
        _structureUsesRef = null;

        try
        {
            action();
        }
        finally
        {
            _activeStyleSheet = previousSheet;
            _activeTypeDefinitions = previousDefinitions;
            _activeUserBlocks = previousUserBlocks;
            _activeDocument = previousDocument;
            _activeIcons = previousIcons;
            _documentRefIndex = previousRefIndex;
            _structureUsesRef = previousStructureUsesRef;
        }
    }

    /// <summary>
    /// 渲染单个块，与整篇渲染共用同一套样式/模板/图标/结构/签名。
    /// 供画布检查器等“只要一个节点 HTML”的调用方使用，杜绝第二套求值。
    /// </summary>
    public string RenderFragment(MarkdownDocument document, MarkdownNode node) =>
        RenderFragment(document, node, RenderFragmentOptions.None);

    /// <summary>
    /// 渲染单个块并应用降级开关（画布旁路阶段的实验态）。
    /// 开关只关闭渲染器本来会做的加工，不改变模板选择之外的任何求值，
    /// 因此“无开关”调用与整篇渲染仍然逐字节一致。
    /// </summary>
    public string RenderFragment(
        MarkdownDocument document,
        MarkdownNode node,
        RenderFragmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(node);

        var icons = _iconOptions.Snapshot();
        var definitions = GetDefinitions(document);
        var ctx = CreateContext(document, assembleHtml: true, icons, definitions, HasKindScopedElements(document.Root));

        ExecuteWithDocumentState(document, icons, () =>
        {
            var previous = _fragmentOptions;
            var previousOverrideNode = _structureOverrideNode;
            _fragmentOptions = options ?? RenderFragmentOptions.None;
            _structureOverrideNode = _fragmentOptions.Structure is null ? null : node;
            try
            {
                RenderBlockBoundary(node, ctx);
            }
            finally
            {
                _fragmentOptions = previous;
                _structureOverrideNode = previousOverrideNode;
            }
        });

        // 单块渲染不动整篇块缓存：不清扫，避免把整篇预览的热块踢掉。
        return ctx.Html?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// 描述该节点最终使用的模板（部件树 + 类名 + 根部件内联样式 + 图标/标题槽成品 HTML）。
    /// 这是检查器“看结构”的唯一来源：模板、变体与 kind 解析都来自渲染器本身，
    /// 不再维护第二份模板表。
    /// </summary>
    public ElementTemplateSnapshot? DescribeTemplate(MarkdownDocument document, MarkdownNode node)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(node);

        var icons = _iconOptions.Snapshot();
        var definitions = GetDefinitions(document);
        ElementTemplateSnapshot? snapshot = null;

        ExecuteWithDocumentState(document, icons, () =>
        {
            var variant = ResolveTemplateVariant(node);
            var template = node.Type == NodeType.Custom
                ? ResolveStructureTemplate(node, variant, [])
                : null;

            // 数据面优先：applies-to 定义命中时覆盖内置描述（选择器不命中则返回 null，保持原样）。
            template = ResolveBuiltInDefinitionTemplate(node, variant) ?? template;

            // 描述与渲染同源：选择器命中的结构优先，未命中回落 (类型, 变体) 注册表。
            template ??= ResolveBaseTemplate(node, variant);

            if (template is null)
            {
                return;
            }

            snapshot = new ElementTemplateSnapshot(
                variant.ToString(),
                ElementKind.Resolve(node),
                BuildSnapshotPart(template.Root, template, node));
        });

        return snapshot;
    }

    private ElementPartSnapshot BuildSnapshotPart(PartTemplate part, ElementTemplate template, MarkdownNode node)
    {
        var extraClass = part.ExtraClass?.Invoke(node);
        var classValue = part.ConditionalClass is not null
            ? part.ConditionalClass(node)
            : part.Class;
        var cssClass = string.IsNullOrEmpty(classValue)
            ? extraClass
            : string.IsNullOrEmpty(extraClass) ? classValue : classValue + " " + extraClass;
        var declaredTag = part.Tag ?? part.TagTemplate?.Invoke(node);

        string? slotHtml = null;
        if (declaredTag is not null
            && part.Part is PartTokens.Icon or PartTokens.TitleText
            && template.Slots.TryGetValue(part.Part, out var writer))
        {
            var buffer = new StringBuilder();
            writer(new TemplateRenderContext
            {
                Node = node,
                CurrentNode = node,
                Html = buffer,
                Diagnostics = new List<DiagnosticMessage>(),
            });
            slotHtml = buffer.ToString();
        }

        var children = new List<ElementPartSnapshot>(part.Children.Count);
        foreach (var child in part.Children)
        {
            children.Add(BuildSnapshotPart(child, template, node));
        }

        return new ElementPartSnapshot(
            part.Part,
            ResolveDescriptionTag(declaredTag, node),
            part.Role,
            part.Addressable,
            cssClass,
            part.Role == ElementPartRole.Root ? BuildNodeInlineCss(node, part.ExtraCss?.Invoke(node)) : null,
            slotHtml,
            children);
    }

    /// <summary>
    /// 描述里的标签模板按节点取值展开（<c>h{level}</c>、<c>{header-tag}</c>）。
    /// 只解析已登记的只读派生字段；字段未知时原样返回，绝不猜测或回退到按类型分支。
    /// </summary>
    private static string? ResolveDescriptionTag(string? tag, MarkdownNode node)
    {
        if (tag is null || tag.IndexOf('{') < 0
            || !UserBlockStructureRules.TryGetTagField(tag, out var field))
        {
            return tag;
        }

        var value = ResolveDerivedField(node, field);
        if (value is null)
        {
            return tag;
        }

        UserBlockStructureRules.TryGetTagPrefix(tag, out var prefix);
        return prefix + value;
    }

    /// <summary>只读派生字段：只允许从节点已解析语义取值，不从定义层传入。</summary>
    private static string? ResolveDerivedField(MarkdownNode node, string field) => field switch
    {
        "level" => Math.Clamp(node.Level ?? 1, 1, 6).ToString(CultureInfo.InvariantCulture),
        "header-tag" => node.IsTableHeader ? "th" : "td",
        _ => null,
    };

    private RenderContext CreateContext(
        MarkdownDocument document,
        bool assembleHtml,
        IconConfig icons,
        ElementDefinitionFingerprintSet definitions,
        bool hasKindScopedElements) =>
        new()
        {
            Epoch = CreateEpoch(definitions, icons, hasKindScopedElements),
            Definitions = definitions,
            Icons = icons,
            ScopeIconsPerBlock = hasKindScopedElements,
            StyleRevision = document.StyleRevision,
            Diagnostics = new List<DiagnosticMessage>(),
            AssembleHtml = assembleHtml,
            Html = assembleHtml
                ? new StringBuilder(Math.Max(1024, (document.SourceMarkdown?.Length ?? 512) * 2))
                : null
        };

    private void RenderBlockBoundaries(MarkdownNode parent, RenderContext ctx)
    {
        for (int i = 0; i < parent.Children.Count; i++)
        {
            var child = parent.Children[i];
            if (child.Type is NodeType.Document or NodeType.Section)
            {
                // 透明容器自身不产生元素，其子节点继续作为缓存边界。
                RenderBlockBoundaries(child, ctx);
            }
            else
            {
                RenderBlockBoundary(child, ctx);
            }
        }
    }

    private void RenderBlockBoundary(MarkdownNode node, RenderContext ctx)
    {
        ctx.Counters = ctx.Counters with { Total = ctx.Counters.Total + 1 };
        if (TryReusePrefixBlock(node, ctx))
        {
            return;
        }

        BlockEntry? entry;
        lock (_blockCacheGate)
        {
            _blockCache.TryGetValue(node.Id, out entry);
        }

        // 引用级签名备忘：同节点对象 + 同纪元 + 同定义聚合指纹 + 同修订号 + 同图标快照时，
        // 子树内容与元素定义都未变，跳过整棵子树哈希（大文档流式追加帧的主要 O(N) 成本之一）。
        // 定义聚合指纹兜住“定义被就地修改但调用方没递增修订号”；修订号兜住
        // “节点属性/局部样式被就地修改”（这类修改不换节点引用，也不改变定义）；
        // 图标戳在 kind 级图标作用域下兜住“图标配置变了但纪元不含图标”的情况——
        // 只让备忘失效、重新算签名，块是否复用仍由签名决定，因此不会扩大失效范围。
        string signature;
        if (entry is not null
            && entry.Epoch.Equals(ctx.Epoch)
            && string.Equals(entry.DefinitionStamp, ctx.Definitions.Aggregate, StringComparison.Ordinal)
            && entry.StyleRevision == ctx.StyleRevision
            && string.Equals(entry.IconStamp, IconStampFor(ctx), StringComparison.Ordinal)
            && ReferenceEquals(entry.Node, node)
            // 引用派生值（序号/slug）依赖整篇文档里同 kind 的出现顺序：
            // 前面插入/删除同 kind 块不改变本节点的字节，却会改变引用值，必须跳过备忘重算签名。
            && !NodeStructureUsesRef(node))
        {
            ctx.Counters = ctx.Counters with { MemoHits = ctx.Counters.MemoHits + 1 };
            signature = entry.Signature;
        }
        else
        {
            ctx.Counters = ctx.Counters with { MemoMisses = ctx.Counters.MemoMisses + 1 };
            signature = ComputeBlockSignature(node, ctx);
        }

        if (entry is not null && entry.Signature == signature)
        {
            if (ctx.AssembleHtml)
            {
                ctx.Html!.Append(entry.Html);
            }

            ctx.LiveIds.Add(node.Id);
            ctx.OrderedIds.Add(node.Id);
            ctx.FrameBlocks.Add(new FrameBlock
            {
                Id = node.Id,
                Fragment = entry.Html,
                Signature = signature,
                Node = node,
                PreviousSignature = entry.Signature
            });
            ctx.Counters = ctx.Counters with { FromCache = ctx.Counters.FromCache + 1 };
            return;
        }

        var rawLength = node.RawMarkdown?.Length ?? 0;
        var blockBuffer = new StringBuilder(Math.Max(64, rawLength * 2));
        var blockDiagnostics = new List<DiagnosticMessage>();
        RenderNode(node, blockBuffer, blockDiagnostics);
        ctx.Diagnostics.AddRange(blockDiagnostics);

        var rawFragment = blockBuffer.ToString();
        var fragmentHtml = InjectBlockSignature(rawFragment, signature);

        lock (_blockCacheGate)
        {
            if (fragmentHtml is null)
            {
                _blockCache.Remove(node.Id);
            }
            else
            {
                _blockCache[node.Id] = new BlockEntry(
                    signature,
                    fragmentHtml,
                    node,
                    ctx.Epoch,
                    ctx.Definitions.Aggregate,
                    ctx.StyleRevision,
                    IconStampFor(ctx));
            }
        }

        if (fragmentHtml is null)
        {
            // 空片段或非元素开头：无法按 id 寻址，标记后由 RenderIncremental 回退全量。
            if (ctx.AssembleHtml)
            {
                ctx.Html!.Append(rawFragment);
            }

            ctx.LiveIds.Add(node.Id);
            ctx.SawNonCacheableBlock = true;
            return;
        }

        if (ctx.AssembleHtml)
        {
            ctx.Html!.Append(fragmentHtml);
        }

        ctx.LiveIds.Add(node.Id);
        ctx.OrderedIds.Add(node.Id);
        ctx.FrameBlocks.Add(new FrameBlock
        {
            Id = node.Id,
            Fragment = fragmentHtml,
            Signature = signature,
            Node = node,
            PreviousSignature = entry?.Signature
        });
    }

    // 已发布的完整块按 64 个一组冻结。整组都落在未断开的复用前缀里时直接共享数组，
    // 只为变化边界重新分配。已发布的组不能原地改：本帧补丁失败时还要读上一帧。
    private const int PrefixChunkShift = 6;
    private const int PrefixChunkSize = 1 << PrefixChunkShift;
    private const int PrefixChunkMask = PrefixChunkSize - 1;

    /// <summary>
    /// 上一帧具体块的扁平快照。流式解析按引用复用未变前缀时，下一帧直接复用片段，
    /// 不再逐块加锁、查缓存或分配 <see cref="FrameBlock"/>。
    /// </summary>
    private sealed class PrefixFrame
    {
        public required MarkdownDocument Document { get; init; }
        public required CacheEpoch Epoch { get; init; }
        public required long StyleRevision { get; init; }
        public required string DefinitionAggregate { get; init; }
        public required string IconStamp { get; init; }
        public required bool ScopeIconsPerBlock { get; init; }
        public required MarkdownNode[][] Nodes { get; init; }
        public required string[][] Ids { get; init; }
        public required string[][] Fragments { get; init; }
        public required string[][] Signatures { get; init; }
        public required int Count { get; init; }

        public MarkdownNode NodeAt(int index)
            => Nodes[index >> PrefixChunkShift][index & PrefixChunkMask];

        public string IdAt(int index)
            => Ids[index >> PrefixChunkShift][index & PrefixChunkMask];

        public string FragmentAt(int index)
            => Fragments[index >> PrefixChunkShift][index & PrefixChunkMask];

        public string SignatureAt(int index)
            => Signatures[index >> PrefixChunkShift][index & PrefixChunkMask];
    }

    private void BeginPrefixReuse(MarkdownDocument document, RenderContext ctx)
    {
        PrefixFrame? frame;
        lock (_blockCacheGate)
        {
            frame = _prefixFrame;
        }

        _reuseFrame = frame;
        _prefixCursor = 0;
        _prefixOpen = frame is not null
            && ReferenceEquals(frame.Document, document)
            && frame.Epoch.Equals(ctx.Epoch)
            && frame.StyleRevision == ctx.StyleRevision
            && string.Equals(frame.DefinitionAggregate, ctx.Definitions.Aggregate, StringComparison.Ordinal)
            && string.Equals(frame.IconStamp, IconStampFor(ctx), StringComparison.Ordinal)
            && frame.ScopeIconsPerBlock == ctx.ScopeIconsPerBlock;
    }

    /// <summary>
    /// 前缀未断且节点仍是上一帧那个对象时，片段与签名都不可能变。
    /// 引用结构依赖同 kind 出现顺序，不能纳入这条路径。
    /// </summary>
    private bool TryReusePrefixBlock(MarkdownNode node, RenderContext ctx)
    {
        if (!_prefixOpen || _reuseFrame is not { } frame)
        {
            return false;
        }

        if (_prefixCursor >= frame.Count
            || !ReferenceEquals(frame.NodeAt(_prefixCursor), node)
            || NodeStructureUsesRef(node))
        {
            _prefixOpen = false;
            return false;
        }

        var index = _prefixCursor++;
        ctx.Counters = ctx.Counters with
        {
            MemoHits = ctx.Counters.MemoHits + 1,
            FromCache = ctx.Counters.FromCache + 1
        };
        if (ctx.AssembleHtml)
        {
            ctx.Html!.Append(frame.FragmentAt(index));
        }

        var id = frame.IdAt(index);
        ctx.LiveIds.Add(id);
        ctx.OrderedIds.Add(id);
        return true;
    }

    private void PublishPrefixFrame(MarkdownDocument document, RenderContext ctx)
    {
        if (ctx.SawNonCacheableBlock || ctx.OrderedIds.Count != _prefixCursor + ctx.FrameBlocks.Count)
        {
            lock (_blockCacheGate)
            {
                _prefixFrame = null;
            }

            return;
        }

        if (_prefixCursor == ctx.OrderedIds.Count && _reuseFrame is not null && _prefixOpen)
        {
            return;
        }

        var count = ctx.OrderedIds.Count;
        var chunkCount = (count + PrefixChunkMask) >> PrefixChunkShift;
        var nodes = new MarkdownNode[chunkCount][];
        var ids = new string[chunkCount][];
        var fragments = new string[chunkCount][];
        var signatures = new string[chunkCount][];
        var reuse = _prefixCursor > 0 ? _reuseFrame : null;
        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            var start = chunk << PrefixChunkShift;
            var length = Math.Min(PrefixChunkSize, count - start);
            if (reuse is not null
                && length == PrefixChunkSize
                && start + PrefixChunkSize <= _prefixCursor
                && start + PrefixChunkSize <= reuse.Count
                && chunk < reuse.Nodes.Length
                && reuse.Nodes[chunk].Length == PrefixChunkSize)
            {
                nodes[chunk] = reuse.Nodes[chunk];
                ids[chunk] = reuse.Ids[chunk];
                fragments[chunk] = reuse.Fragments[chunk];
                signatures[chunk] = reuse.Signatures[chunk];
                continue;
            }

            var chunkNodes = new MarkdownNode[length];
            var chunkIds = new string[length];
            var chunkFragments = new string[length];
            var chunkSignatures = new string[length];
            var copied = reuse is null ? 0 : Math.Min(length, Math.Max(0, _prefixCursor - start));
            for (var i = 0; i < copied; i++)
            {
                var index = start + i;
                chunkNodes[i] = reuse!.NodeAt(index);
                chunkIds[i] = reuse.IdAt(index);
                chunkFragments[i] = reuse.FragmentAt(index);
                chunkSignatures[i] = reuse.SignatureAt(index);
            }

            for (var i = copied; i < length; i++)
            {
                var block = ctx.FrameBlocks[start + i - _prefixCursor];
                chunkNodes[i] = block.Node;
                chunkIds[i] = block.Id;
                chunkFragments[i] = block.Fragment;
                chunkSignatures[i] = block.Signature;
            }

            nodes[chunk] = chunkNodes;
            ids[chunk] = chunkIds;
            fragments[chunk] = chunkFragments;
            signatures[chunk] = chunkSignatures;
        }

        var published = new PrefixFrame
        {
            Document = document,
            Epoch = ctx.Epoch,
            StyleRevision = ctx.StyleRevision,
            DefinitionAggregate = ctx.Definitions.Aggregate,
            IconStamp = IconStampFor(ctx),
            ScopeIconsPerBlock = ctx.ScopeIconsPerBlock,
            Nodes = nodes,
            Ids = ids,
            Fragments = fragments,
            Signatures = signatures,
            Count = count
        };

        lock (_blockCacheGate)
        {
            _prefixFrame = published;
        }
    }

    /// <summary>
    /// 只在“公共前缀之后的旧块全部消失”时生成补丁。此时幸存块相对顺序不变，
    /// 与 <see cref="BuildPatchOps"/> 一样只有 Remove 和 Upsert，没有 Move。
    /// 中间插入、删除或换序返回 false，调用方回退全量比较。
    /// </summary>
    private static bool TryBuildSuffixPatch(
        IncrementalBaseline baseline,
        RenderContext ctx,
        int prefixCount,
        out List<PreviewPatchOp> ops,
        out long payloadChars)
    {
        ops = new List<PreviewPatchOp>();
        payloadChars = 0;
        var previous = baseline.OrderedIds;
        var current = ctx.OrderedIds;
        if (prefixCount < 0 || ctx.FrameBlocks.Count != current.Count - prefixCount)
        {
            return false;
        }

        var common = 0;
        var limit = Math.Min(previous.Count, current.Count);
        while (common < limit
               && string.Equals(previous[common], current[common], StringComparison.Ordinal))
        {
            common++;
        }

        // 复用前缀必须也是基线 Id 前缀；否则幸存块的 PreviousSignature 不在这条路径上。
        if (common < prefixCount)
        {
            return false;
        }

        for (var i = common; i < previous.Count; i++)
        {
            if (ctx.LiveIds.Contains(previous[i]))
            {
                return false;
            }
        }

        for (var i = common; i < previous.Count; i++)
        {
            ops.Add(new PreviewPatchOp(previous[i], PreviewPatchOpKind.Remove));
            payloadChars += EstimatedOpOverhead;
        }

        for (var i = prefixCount; i < current.Count; i++)
        {
            var block = ctx.FrameBlocks[i - prefixCount];
            if (i < common && !block.SignatureChanged)
            {
                continue;
            }

            var afterId = i > 0 ? current[i - 1] : null;
            ops.Add(new PreviewPatchOp(block.Id, PreviewPatchOpKind.Upsert, afterId, block.Fragment));
            payloadChars += EstimatedOpOverhead + block.Fragment.Length;
        }

        return true;
    }

    private static List<FrameBlock> MaterializeFrameBlocks(
        PrefixFrame? reuseFrame,
        int prefixCount,
        List<FrameBlock> tail)
    {
        if (prefixCount == 0 || reuseFrame is null)
        {
            return tail;
        }

        var merged = new List<FrameBlock>(prefixCount + tail.Count);
        for (var i = 0; i < prefixCount; i++)
        {
            merged.Add(new FrameBlock
            {
                Id = reuseFrame.IdAt(i),
                Fragment = reuseFrame.FragmentAt(i),
                Signature = reuseFrame.SignatureAt(i),
                Node = reuseFrame.NodeAt(i),
                PreviousSignature = reuseFrame.SignatureAt(i)
            });
        }

        merged.AddRange(tail);
        return merged;
    }

    private static long PrefixFragmentChars(PrefixFrame? reuseFrame, int prefixCount)
    {
        if (reuseFrame is null || prefixCount <= 0)
        {
            return 0;
        }

        long total = 0;
        var count = Math.Min(prefixCount, reuseFrame.Count);
        for (var i = 0; i < count; i++)
        {
            total += reuseFrame.FragmentAt(i).Length;
        }

        return total;
    }

    // 比较上一帧与本帧的有序块序列，生成最小块级操作：先删除消失块，
    // 再按文档顺序 upsert（新增/签名变化）或 move（仅幸存块相对顺序改变）。
    // 被删块不留空位、新插入块按 after 锚点自然挤位，都不产生 move。
    private static List<PreviewPatchOp> BuildPatchOps(
        IncrementalBaseline baseline,
        List<string> orderedIds,
        List<FrameBlock> blocks,
        out long payloadChars)
    {
        var prevIds = baseline.OrderedIds;
        var prevIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < prevIds.Count; i++)
        {
            prevIndex[prevIds[i]] = i;
        }

        var currentIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < orderedIds.Count; i++)
        {
            currentIndex[orderedIds[i]] = i;
        }

        // 旧序列中每个幸存块的前一个幸存块（跳过本帧将被删除的块）。
        var oldSurvivorPredecessor = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? lastOldSurvivor = null;
        foreach (var id in prevIds)
        {
            if (currentIndex.ContainsKey(id))
            {
                oldSurvivorPredecessor[id] = lastOldSurvivor;
                lastOldSurvivor = id;
            }
        }

        var ops = new List<PreviewPatchOp>();
        payloadChars = 0;

        foreach (var id in prevIds)
        {
            if (!currentIndex.ContainsKey(id))
            {
                ops.Add(new PreviewPatchOp(id, PreviewPatchOpKind.Remove));
                payloadChars += EstimatedOpOverhead;
            }
        }

        string? lastCurrentSurvivor = null;
        for (var i = 0; i < blocks.Count; i++)
        {
            var fb = blocks[i];
            var afterId = i > 0 ? orderedIds[i - 1] : null;
            var existedBefore = prevIndex.ContainsKey(fb.Id);

            if (!existedBefore || fb.SignatureChanged)
            {
                ops.Add(new PreviewPatchOp(fb.Id, PreviewPatchOpKind.Upsert, afterId, fb.Fragment));
                payloadChars += EstimatedOpOverhead + fb.Fragment.Length;
                if (existedBefore)
                {
                    lastCurrentSurvivor = fb.Id;
                }

                continue;
            }

            // 幸存且未变：相对其它幸存块的前驱是否改变。
            var oldPredecessor = oldSurvivorPredecessor.GetValueOrDefault(fb.Id);
            if (!string.Equals(oldPredecessor, lastCurrentSurvivor, StringComparison.Ordinal))
            {
                ops.Add(new PreviewPatchOp(fb.Id, PreviewPatchOpKind.Move, afterId));
                payloadChars += EstimatedOpOverhead;
            }

            lastCurrentSurvivor = fb.Id;
        }

        return ops;
    }

    private void SweepStaleBlockFragments(HashSet<string> liveBlockIds)
    {
        List<string>? stale = null;

        lock (_blockCacheGate)
        {
            foreach (var key in _blockCache.Keys)
            {
                if (!liveBlockIds.Contains(key))
                {
                    (stale ??= new List<string>()).Add(key);
                }
            }

            if (stale is not null)
            {
                foreach (var key in stale)
                {
                    _blockCache.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// 图标戳：只在 kind 级图标作用域下有意义（纪元不含图标指纹）。
    /// 改动任一图标都会让戳失效，从而重算签名；块是否复用仍由签名逐块决定。
    /// </summary>
    private static string IconStampFor(RenderContext ctx) =>
        ctx.ScopeIconsPerBlock ? ctx.Icons.Fingerprint : string.Empty;

    /// <summary>
    /// 块签名 = 文档级定义/图标指纹 + 块边界之上的祖先定义 + 整块子树
    /// （节点局部样式/类型定义/命名样式/类型/层级/属性/Text/RawMarkdown，递归子孙）
    /// 的 FNV-1a 64 位哈希。任何可见输入变化都会改签名；无关类型的定义变化不会。
    /// </summary>
    private string ComputeBlockSignature(MarkdownNode node, RenderContext ctx)
    {
        var hash = HashString(FnvOffsetBasis, ctx.Epoch.Prefix);

        // 块边界之上的 Document/Section 是透明容器，其类型/命名/局部样式会沿继承链
        // 传进块内，必须折叠进签名；否则祖先定义变化时后代块不会失效。
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            hash = FoldNodeStyleInputs(hash, ancestor, ctx);
        }

        hash = FoldNodeSignature(hash, node, ctx);

        // 引用派生值也是可见输出的一部分：同 kind 的出现序号由文档顺序决定，
        // 折叠进来后“前面插入同 kind 块”会只让后续引用块换签名，
        // 而改 A 定义不会动 B 的引用值，失效范围仍按 kind 收敛。
        if (NodeStructureUsesRef(node) && CurrentDocumentRefIndex()?.Find(node) is { } refEntry)
        {
            hash = Mix(hash, (ulong)refEntry.Ordinal);
            hash = HashString(hash, refEntry.Slug);
            hash = HashString(hash, refEntry.Backrefs);
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    // 递归折叠整棵子树：未闭合围栏等块节点自身 RawMarkdown 只有开栏行
    // （转换器伪 Range），实际内容在子节点上，不递归会命中陈旧片段。
    private ulong FoldNodeSignature(
        ulong hash,
        MarkdownNode node,
        RenderContext ctx)
    {
        hash = FoldNodeStyleInputs(hash, node, ctx);

        hash = Mix(hash, (ulong)(int)node.Type);
        hash = Mix(hash, (ulong)(node.Level ?? 0));
        hash = Mix(hash, (ulong)(node.IsSynthetic ? 1 : 0));
        hash = HashString(hash, CanonicalNodeText(node));
        hash = HashString(hash, CanonicalRawMarkdown(node));

        if (node.Attributes.Count > 0)
        {
            var sorted = new List<KeyValuePair<string, string>>(node.Attributes);
            sorted.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

            foreach (var pair in sorted)
            {
                hash = HashString(hash, pair.Key);
                hash = Mix(hash, '=');
                hash = HashString(hash, pair.Value);
                hash = Mix(hash, ';');
            }
        }

        foreach (var child in node.Children)
        {
            hash = FoldNodeSignature(hash, child, ctx);
        }

        return hash;
    }

    /// <summary>
    /// 用户块头文本：<c>node.Text</c> 是开围栏首词及其后的属性原文，
    /// 签名折叠时同样把别名规范成本名，保证同一元素的不同写法产出同一签名。
    /// </summary>
    private string? CanonicalNodeText(MarkdownNode node)
    {
        var text = node.Text;
        if (string.IsNullOrEmpty(text) || !TryGetAliasedDefinition(node, out var definition))
        {
            return text;
        }

        var pos = 0;
        while (pos < text.Length && !char.IsWhiteSpace(text[pos])) pos++;
        if (pos == 0) return text;

        var keyword = text[..pos];
        return string.Equals(keyword, definition.Name, StringComparison.Ordinal)
               || !definition.MatchesKeyword(keyword)
            ? text
            : string.Concat(definition.Name, text[pos..]);
    }

    private bool TryGetAliasedDefinition(MarkdownNode node, out UserBlockDefinition definition)
    {
        definition = null!;
        if (node.Type != NodeType.Custom) return false;

        var blocks = _activeUserBlocks;
        if (blocks is null || blocks.Count == 0) return false;

        var kind = ElementKind.NormalizeToken(ElementKind.Resolve(node));
        return kind is not null
               && blocks.TryGetValue(kind, out definition!)
               && definition.Aliases.Count > 0;
    }

    /// <summary>
    /// 签名折叠用的块源码：把开围栏首词从“实际写出的别名”规范成本名。
    /// 别名只是同一元素的另一种写法，渲染产物必须逐字节一致，块签名也不该因写法不同而变；
    /// 不含别名的定义直接返回原串，历史定义的签名逐字节不变。
    /// </summary>
    private string? CanonicalRawMarkdown(MarkdownNode node)
    {
        var raw = node.RawMarkdown;
        if (raw is null || !TryGetAliasedDefinition(node, out var definition))
        {
            return raw;
        }

        // 围栏字符与别名关键字都是写法差异（~~~ 与 :::、bc 与 briefcard 是同一个元素的合法写法），
        // 一并规范成定义的主围栏字符与本名，否则同一块体换个写法签名就会漂移。
        // 开围栏行带关键字，收围栏行只有围栏字符，两处都要规范化。
        var lines = raw.Split('\n');
        var changed = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var carriage = line.EndsWith('\r') ? "\r" : string.Empty;
            var text = carriage.Length == 0 ? line : line[..^1];

            var contentEnd = text.Length;
            while (contentEnd > 0 && text[contentEnd - 1] == ' ') contentEnd--;
            var content = text[..contentEnd];

            var pos = 0;
            while (pos < 3 && pos < content.Length && content[pos] == ' ') pos++;
            var fenceStart = pos;
            while (pos < content.Length && IsDeclaredFenceChar(definition, content[pos])) pos++;
            if (pos == fenceStart) continue;
            var fenceLength = pos - fenceStart;

            string replacement;
            if (pos == content.Length)
            {
                replacement = string.Concat(
                    content[..fenceStart],
                    new string(definition.FenceChar, fenceLength));
            }
            else
            {
                if (index != 0) continue;

                var keywordStart = pos;
                while (pos < content.Length && !char.IsWhiteSpace(content[pos])) pos++;
                if (pos == keywordStart) continue;

                var keyword = content[keywordStart..pos];
                if (string.Equals(keyword, definition.Name, StringComparison.Ordinal)
                    || !definition.MatchesKeyword(keyword))
                {
                    continue;
                }

                replacement = string.Concat(
                    content[..fenceStart],
                    new string(definition.FenceChar, fenceLength),
                    definition.Name,
                    content[pos..]);
            }

            lines[index] = replacement + text[contentEnd..] + carriage;
            changed = true;
        }

        return changed ? string.Join('\n', lines) : raw;
    }

    private static bool IsDeclaredFenceChar(UserBlockDefinition definition, char value)
    {
        for (var index = 0; index < definition.EffectiveFenceChars.Count; index++)
        {
            if (definition.EffectiveFenceChars[index] == value) return true;
        }

        return false;
    }

    /// <summary>
    /// 折叠节点样式输入：局部样式、类型定义（含 kind 限定）、命名样式（StyleId）各自的指纹。
    /// 都是“语义规范串”，定义文件的书写顺序不会改变结果。
    /// 交互态（选中/悬停等）不属于这些输入，因此天然不进签名。
    /// </summary>
    private static ulong FoldNodeStyleInputs(
        ulong hash,
        MarkdownNode node,
        RenderContext ctx)
    {
        hash = Mix(hash, NodeLocalStyleMarker);
        hash = HashString(hash, ElementDefinitionFingerprint.DescribeNodeStyle(node.LocalStyle));

        var kind = ElementKind.Resolve(node);
        if (ctx.Definitions.TryGetTypeFingerprint(node.Type, kind, out var typeFingerprint))
        {
            hash = Mix(hash, NodeTypeDefinitionMarker);
            hash = HashString(hash, typeFingerprint);
        }

        if (ctx.Definitions.TryGetStyleFingerprint(node.StyleId, out var styleFingerprint))
        {
            hash = Mix(hash, NodeNamedStyleMarker);
            hash = HashString(hash, styleFingerprint);
        }

        if (ctx.ScopeIconsPerBlock)
        {
            hash = FoldNodeIcons(hash, node, kind, ctx.Icons);
        }

        return hash;
    }

    /// <summary>
    /// kind 级图标作用域下，把节点实际用到的字形折叠进签名：改一个 kind 的图标
    /// 只让该 kind 的块失效。提示块与自定义块各取自己的字形体系；
    /// 其它类型不消费图标，不折叠。节点上的 <c>icon</c> 属性覆盖已随属性折叠进签名。
    /// </summary>
    private static ulong FoldNodeIcons(
        ulong hash,
        MarkdownNode node,
        string? kind,
        IconConfig icons)
    {
        string? glyph = null;

        if (node.Type == NodeType.Custom)
        {
            glyph = icons.GetCustomBlockIcon(kind ?? AdmonitionCatalog.DefaultCustomBlockKind);
        }
        else if (node.Type == NodeType.Quote && node.Attributes.ContainsKey("admonition"))
        {
            var safeKind = node.Attributes.TryGetValue("admonition", out var admonition)
                           && AdmonitionCatalog.AdmonitionKinds.Contains(admonition)
                ? admonition
                : AdmonitionCatalog.DefaultAdmonitionKind;
            glyph = icons.GetAdmonitionIcon(safeKind);
        }

        if (glyph is null)
        {
            return hash;
        }

        hash = Mix(hash, NodeKindIconMarker);
        hash = HashString(hash, kind);
        hash = Mix(hash, '=');
        return HashString(hash, glyph);
    }

    private const ulong NodeLocalStyleMarker = 0x4C53_5459_4C45UL; // 'LSTYLE'
    private const ulong NodeTypeDefinitionMarker = 0x5459_5045_4446UL; // 'TYPEDF'
    private const ulong NodeNamedStyleMarker = 0x4E41_4D45_4446UL; // 'NAMEDF'
    private const ulong NodeKindIconMarker = 0x4B49_4E44_4943UL; // 'KINDIC'

    private static string? InjectBlockSignature(string fragment, string signature)
    {
        if (fragment.Length == 0 || fragment[0] != '<')
        {
            return null;
        }

        var tagEnd = fragment.IndexOf('>');
        if (tagEnd <= 1)
        {
            return null;
        }

        return string.Concat(
            fragment.AsSpan(0, tagEnd),
            $" data-block-sig=\"{signature}\"",
            fragment.AsSpan(tagEnd));
    }

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static ulong Mix(ulong hash, ulong value)
    {
        hash ^= value;
        hash *= FnvPrime;
        return hash;
    }

    private static ulong HashString(ulong hash, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return hash;
        }

        foreach (var c in value)
        {
            hash ^= (ulong)c & 0xFFUL;
            hash *= FnvPrime;
            hash ^= ((ulong)c >> 8) & 0xFFUL;
            hash *= FnvPrime;
        }

        return hash;
    }


    /// <summary>定义缺失时的固定诊断码：迁移过的类型不得静默回退。</summary>
    internal const string DefinitionMissingDiagnosticCode = "ELEMENT_DEFINITION_MISSING";

    /// <summary>发射策略自相矛盾（既有标签又声明不发射）时的固定诊断码：整块拒绝，不静默丢弃。</summary>
    internal const string EmitConflictDiagnosticCode = "ELEMENT_EMIT_CONFLICT";

    /// <summary>
    /// 已完成定义驱动迁移的类型集合。它们不再有 <c>case NodeType.</c> 直出分支，
    /// 一旦定义缺失/停用，必须留下明确诊断，而不是悄悄按默认路径渲染子节点。
    /// 这里只做“缺定义即报错”的守卫，不参与任何类型专属渲染逻辑。
    /// </summary>
    private static readonly HashSet<NodeType> DefinitionDrivenTypes =
    [
        NodeType.Paragraph,
        NodeType.LineBreak,
        NodeType.ThematicBreak,
        NodeType.FootnoteGroup,
        NodeType.UnorderedList,
        NodeType.OrderedList,
        NodeType.ListItem,
        NodeType.Strong,
        NodeType.Emphasis,
        NodeType.Delete,
        NodeType.Subscript,
        NodeType.Superscript,
        NodeType.Inserted,
        NodeType.Marked,
        NodeType.InlineCode,
        NodeType.Heading,
        NodeType.HtmlBlock,
        NodeType.Footnote,
        NodeType.FootnoteLink,
        NodeType.Link,
        NodeType.Image,
        NodeType.Text,
        NodeType.CodeBlock,
        NodeType.TableRow,
        NodeType.TableCell,
        NodeType.TaskListItem,
        NodeType.Table,
        NodeType.Quote,
        NodeType.MathInline,
        NodeType.MathBlock,
        NodeType.Mermaid,
        NodeType.ImageMatrix,
        NodeType.QrCode,
        NodeType.Radar,
        NodeType.Timeline,
        NodeType.Custom,
        NodeType.PoemBlock,
        NodeType.TimelineItem,
        NodeType.YamlFrontMatter,
    ];

    private void RenderNode(MarkdownNode node, StringBuilder html, List<DiagnosticMessage> diagnostics)
    {
        try
        {
            // 已登记结构模板的类型走模板渲染；未登记或变体缺失时回落到既有 switch。
            if (TryRenderTemplate(node, html, diagnostics))
            {
                return;
            }

            if (DefinitionDrivenTypes.Contains(node.Type))
            {
                diagnostics.Add(new DiagnosticMessage
                {
                    Severity = DiagnosticSeverity.Error,
                    Code = DefinitionMissingDiagnosticCode,
                    Message = "类型已迁移为定义驱动但缺少可用定义：" + node.Type,
                });
            }

            switch (node.Type)
            {
                // 行内 HTML：标签名与已放行属性来自节点数据（实例驱动），
                // 不走静态结构描述；净化和白名单判定已经在解析期完成。
                case NodeType.HtmlInline:
                    RenderInlineHtml(node, html, diagnostics);
                    break;

                // 内核层 K3：Document/Section 是透明容器，穿过即可，不产生元素外形。
                case NodeType.Document:
                case NodeType.Section:
                default:
                    RenderChildren(node, html, diagnostics);
                    break;
            }
        }
        catch (StyleResolutionException ex)
        {
            diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Error,
                Code = "STYLE_CYCLE",
                Message = ex.Message,
                Range = node.Range
            });
            RenderChildren(node, html, diagnostics);
        }
    }

    private string? BuildTableRowCascadeVars(MarkdownNode node)
    {
        var resolved = _styleResolver.Resolve(node);

        var bg = resolved.BackgroundColor?.Trim();
        return string.IsNullOrWhiteSpace(bg) ? null : "--row-bg: " + bg + ';';
    }

    // ── 结构模板 ────────────────────────────────────────────────────────

    private bool TryRenderTemplate(
        MarkdownNode node,
        StringBuilder html,
        List<DiagnosticMessage> diagnostics)
    {
        // 旁路「取结构」只跳过“定义/图提供的结构”，内置描述模板仍照常发射：
        // 直出分支已全部删除，内置模板是唯一发射器；连它一起跳过会让诗体、时间线
        // 这类“结构即内容”的类型静默变成空串，属于行为回退。
        var suppressDefinitionStructure = _fragmentOptions.SuppressTemplate;

        // 图给出了结构：用它的部件树替换模板根，槽位与身份属性仍由这里产出。
        if (_fragmentOptions.Structure is { } structure && ReferenceEquals(node, _structureOverrideNode))
        {
            RenderStructureOverride(node, structure, html, diagnostics);
            return true;
        }

        var variant = ResolveTemplateVariant(node);

        ElementTemplate? template = null;

        // 属性驱动的结构选择：只对显式声明选择器的类型生效（表驱动，不按类型名分派）。
        if (_templateSelectors.TryGetValue(node.Type, out var selector))
        {
            var match = ResolveSelectorMatch(selector.Key, node);
            if (!TryGetSelectedTemplate(node.Type, match, variant, out template)
                && selector.FallbackMatch != match)
            {
                return false;
            }
        }

        // 自定义元素：定义里声明了结构时优先用定义结构（同一套模板机制），
        // 未声明结构时仍走既有内置模板，输出逐字节不变。
        template ??= node.Type == NodeType.Custom && !suppressDefinitionStructure
            ? ResolveStructureTemplate(node, variant, diagnostics)
            : null;

        // 内置类型的定义重写：applies-to 命中时优先于选择器命中的内置模板，走同一套结构编译路径。
        if (ResolveBuiltInDefinitionTemplate(node, variant) is { } definitionTemplate)
        {
            template = definitionTemplate;
        }

        if (template is null && !_templates.TryGetValue((node.Type, variant), out template))
        {
            return false;
        }

        // 描述已登记但该类型尚未迁移：只服务 DescribeTemplate，渲染仍走既有直出分支。
        if (!template.RenderEnabled)
        {
            return false;
        }

        // P7：声明“不发射”的部件不得同时带标签——两者矛盾时拒绝整块，不静默丢标签。
        if (FindEmitConflict(template.Root) is { } conflicting)
        {
            diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Error,
                Code = EmitConflictDiagnosticCode,
                Message = "部件同时声明了标签与不发射：" + conflicting,
            });
            return true;
        }

        var context = new TemplateRenderContext
        {
            Node = node,
            CurrentNode = node,
            Html = html,
            Diagnostics = diagnostics,
        };
        RenderPart(context, template.Root, template);
        return true;
    }

    /// <summary>
    /// 用图产出的部件树渲染单块：只替换模板根，槽位填充器复用该类型解析后的模板，
    /// 因此图标、标题、正文与节点身份属性的产出路径与模板渲染完全一致。
    /// </summary>
    private void RenderStructureOverride(
        MarkdownNode node,
        ElementStructureNode structure,
        StringBuilder html,
        List<DiagnosticMessage> diagnostics)
    {
        var variant = ResolveTemplateVariant(node);
        ElementTemplate? baseTemplate = node.Type == NodeType.Custom
            ? ResolveStructureTemplate(node, variant, diagnostics)
            : null;

        baseTemplate ??= ResolveBaseTemplate(node, variant);

        var template = new ElementTemplate
        {
            NodeType = node.Type,
            Variant = variant,
            Root = ToPartTemplate(structure, variant: variant),
        };

        if (baseTemplate is not null)
        {
            foreach (var slot in baseTemplate.Slots)
            {
                template.Slots[slot.Key] = slot.Value;
            }
        }

        var context = new TemplateRenderContext
        {
            Node = node,
            CurrentNode = node,
            Html = html,
            Diagnostics = diagnostics,
        };
        RenderPart(context, template.Root, template);
    }

    private PartTemplate ToPartTemplate(
        ElementStructureNode node,
        Func<MarkdownNode, string?>? extraClass = null,
        Func<MarkdownNode, string?>? extraCss = null,
        ElementTemplateVariant variant = ElementTemplateVariant.Default)
    {
        var styleCss = string.IsNullOrWhiteSpace(node.StyleCss) ? null : node.StyleCss;
        var template = CreateDeclaredPartTemplate(
            node.Part,
            // 插值原文优先：TagTemplate 非空时 Tag 应保持虚拟，由渲染期求值。
            node.TagTemplate ?? node.Tag,
            node.RawClass ?? node.Class,
            style: null,
            node.Addressable,
            node.When,
            node.WhenSource,
            node.Repeat,
            node.RepeatMax,
            node.Generator,
            node.GeneratorParams,
            node.Role,
            extraClass,
            (extraCss, styleCss) switch
            {
                (null, null) => null,
                (null, _) => _ => styleCss,
                (_, null) => extraCss,
                _ => node => extraCss(node) + styleCss,
            },
            variant: variant);

        // 引用、原文槽与重复都是“按数据产出内容”，优先于图上的固定槽内容。
        if (node.Ref is not null)
        {
            var reference = node.Ref;
            template.Content = context => WriteReferenceValue(context, reference);
        }
        else if (node.Raw)
        {
            template.Content = WriteRawContent;
        }
        else if (node.Text is not null && UserBlockStructureRules.TryParseFieldTemplate(node.Text, out var textField))
        {
            template.Content = context => WriteDeclaredTextContent(context, textField);
        }
        else if (node.Repeat is not null)
        {
            template.Content = WriteRepeatEntryContent;
        }
        else if (!string.IsNullOrEmpty(node.SlotHtml))
        {
            var slotHtml = node.SlotHtml;
            template.Content = context => context.Html.Append(slotHtml);
        }

        // 受控 URL 绑定：图路径与声明路径走同一个属性模板与同一个安全判定，
        // 属性名固定为 href/src，值在渲染期按解码后的协议白名单校验并走属性转义。
        if (node.Href is not null && UserBlockStructureRules.TryParseFieldTemplate(node.Href, out var hrefField))
        {
            template.Attributes.Add(new PartAttributeTemplate(
                UserBlockStructureRules.HrefAttribute,
                PartAttributeValueSource.DeclaredFieldUrl,
                hrefField));
        }

        if (node.Src is not null && UserBlockStructureRules.TryParseFieldTemplate(node.Src, out var srcField))
        {
            template.Attributes.Add(new PartAttributeTemplate(
                UserBlockStructureRules.SrcAttribute,
                PartAttributeValueSource.DeclaredFieldUrl,
                srcField));
            template.SelfClosing = true;
        }

        foreach (var child in node.Children)
        {
            template.Children.Add(ToPartTemplate(child, variant: variant));
        }

        return template;
    }

    private ElementTemplateVariant ResolveTemplateVariant(MarkdownNode node)
    {
        var requested = ElementTemplateVariant.Default;
        var kind = ElementKind.Resolve(node);
        var sheet = _activeStyleSheet;
        if (sheet is not null)
        {
            _activeTypeDefinitions ??= TypeDefinitionIndex.Build(sheet);
            if (_activeTypeDefinitions.TryGet(node.Type, kind, out var definition))
            {
                requested = definition.Behavior.TemplateVariant;
            }
        }

        // 变体不在白名单内时回落 Default，由页面侧给出可见提示。
        return TemplateVariantCatalog.Resolve(node.Type, kind, requested).Effective;
    }

    /// <summary>
    /// 定义声明了结构时，把声明树翻译成与内置模板同构的 <see cref="ElementTemplate"/>；
    /// 未声明结构（或净化后不可用）返回 null，由调用方回退内置模板。
    /// </summary>
    private ElementTemplate? ResolveStructureTemplate(
        MarkdownNode node,
        ElementTemplateVariant variant,
        List<DiagnosticMessage> diagnostics)
    {
        var blocks = _activeUserBlocks;
        if (blocks is null || blocks.Count == 0)
        {
            return null;
        }

        var kind = ElementKind.NormalizeToken(ElementKind.Resolve(node));
        if (kind is null || !blocks.TryGetValue(kind, out var definition))
        {
            return null;
        }

        if (ResolveGraphStructure(node, definition) is { } graphStructure)
        {
            if (!HasGraphContentSlot(graphStructure))
            {
                // 图产出的结构同样不能没有正文槽位，否则正文会凭空消失。
                diagnostics.Add(new DiagnosticMessage
                {
                    Severity = DiagnosticSeverity.Warning,
                    Code = "USER_ELEMENTS_GRAPH_NO_CONTENT",
                    Message = $"自定义元素 '{kind}' 的结构图缺少正文槽位（body/content），已回退结构声明或内置模板。",
                    Range = node.Range,
                });
            }
            else
            {
                return BuildGraphStructureTemplate(graphStructure, variant);
            }
        }

        if (definition.Structure.IsEmpty)
        {
            return null;
        }

        var sanitized = UserBlockStructureRules.Sanitize(definition.Structure).Structure;
        if (sanitized.Root is null)
        {
            return null;
        }

        if (!UserBlockStructureRules.HasContentSlot(sanitized))
        {
            // 正文槽位缺失：正文无处可放。明确回退内置模板并给出诊断，不静默丢弃正文。
            diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Warning,
                Code = "USER_ELEMENTS_STRUCTURE_NO_CONTENT",
                Message = $"自定义元素 '{kind}' 的结构缺少正文槽位（body/content），已回退内置模板。",
                Range = node.Range,
            });
            return null;
        }

        var key = (kind, variant, UserBlockStructureRules.Describe(sanitized));
        if (_structureTemplates.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var template = BuildCustomStructureTemplate(sanitized, variant);
        _structureTemplates[key] = template;
        return template;
    }

    /// <summary>
    /// 把结构声明翻译成模板：标签/类名/层级来自定义，槽位填充器复用内置实现
    /// （图标、标题文字、正文子节点）。根部件承担节点属性与内联样式，与内置模板一致。
    /// </summary>
    /// <summary>
    /// 图产出的结构与声明结构共用同一套槽位填充器；kind 类名与强调色仍挂根上。
    /// 槽位覆盖在填充器之后注册，图里写死的槽内容优先。
    /// </summary>
    private ElementTemplate BuildGraphStructureTemplate(ElementStructureNode structure, ElementTemplateVariant variant)
    {
        var root = ToPartTemplate(
            structure,
            ResolveCustomBlockKindClass,
            node => ResolveSemanticAccentCss(node, "--cb-accent"),
            variant);

        var template = new ElementTemplate
        {
            NodeType = NodeType.Custom,
            Variant = variant,
            Root = root,
        };

        template.Slots[PartTokens.Icon] = WriteCustomBlockIcon;
        template.Slots[PartTokens.TitleText] = WriteCustomBlockTitleText;

        PartWriter contentWriter = context => RenderChildren(context.Node, context.Html, context.Diagnostics);
        template.Slots[PartTokens.Body] = contentWriter;
        template.Slots[PartTokens.Content] = contentWriter;

        return template;
    }

    /// <summary>
    /// 求值元素结构图；显式结构覆盖（画布发射）优先，且同一节点在求值途中不再二次求值。
    /// </summary>
    private ElementStructureNode? ResolveGraphStructure(MarkdownNode node, UserBlockDefinition definition)
    {
        if (_fragmentOptions.Structure is not null
            || definition.StructureGraph is not { } graph
            || _activeDocument is not { } document)
        {
            return null;
        }

        if (!ElementGraphStructureResolver.TryEnter(node))
        {
            return null;
        }

        try
        {
            return ElementGraphStructureResolver.TryResolve(document, node, graph);
        }
        finally
        {
            ElementGraphStructureResolver.Exit(node);
        }
    }

    private static bool HasGraphContentSlot(ElementStructureNode node) =>
        node.Part is PartTokens.Body or PartTokens.Content
        || node.Children.Any(HasGraphContentSlot);

    private ElementTemplate BuildCustomStructureTemplate(
        UserBlockStructure structure,
        ElementTemplateVariant variant)
    {
        var root = BuildStructurePart(structure.Root!, isRoot: true);
        var template = new ElementTemplate
        {
            NodeType = NodeType.Custom,
            Variant = variant,
            Root = root,
        };

        template.Slots[PartTokens.Icon] = WriteCustomBlockIcon;

        // 只有 title-text 是“文本槽”；title 是头部容器，只承载子部件。
        // 否则“头部里再放图标/标题文本”的结构会把标题文本重复写进容器开头。
        template.Slots[PartTokens.TitleText] = WriteCustomBlockTitleText;

        PartWriter contentWriter = context => RenderChildren(context.Node, context.Html, context.Diagnostics);
        template.Slots[PartTokens.Body] = contentWriter;
        template.Slots[PartTokens.Content] = contentWriter;

        return template;
    }

    private ElementTemplate? ResolveBuiltInDefinitionTemplate(
        MarkdownNode node,
        ElementTemplateVariant variant)
    {
        if (_activeDocument is not { } document || document.EffectiveUserBlockDefinitions.Count == 0)
        {
            return null;
        }

        var typeName = node.Type.ToString();
        foreach (var definition in document.EffectiveUserBlockDefinitions)
        {
            if (!string.Equals(definition.AppliesTo, typeName, StringComparison.Ordinal)
                || definition.Structure?.Root is not { } rootPart)
            {
                continue;
            }

            // 选择器只按“已声明属性是否存在”选结构，不做类型判断：
            // admonition:present/absent 决定这条定义对当前节点是否适用。
            if (definition.Selector is { } selector
                && !SelectorMatches(selector, node))
            {
                continue;
            }

            var root = BuildStructurePart(rootPart, isRoot: true);

            var template = new ElementTemplate
            {
                NodeType = node.Type,
                Variant = variant,
                Root = root,
                RenderEnabled = definition.RenderEnabled ?? true,
                StructureKey = string.Equals(definition.StructureKey, "poem", StringComparison.Ordinal)
                    ? ElementStructureKey.Poem
                    : ElementStructureKey.None,
            };

            // 槽位写入器沿用该类型的内置模板：重写只换结构，不换“标题/正文/图标”的语义。
            if (ResolveBaseTemplate(node, variant) is { } baseTemplate)
            {
                foreach (var slot in baseTemplate.Slots)
                {
                    template.Slots[slot.Key] = slot.Value;
                }
            }

            // 根部既没写内容槽也没有子部件时，按内置容器语义渲染子节点——与 M(...) 的默认内容一致；
            // 有子部件时正文由子部件承载，不能再叠一层 RenderChildren，否则正文会重复。
            if (root.Content is null && !root.SelfClosing && root.Children.Count == 0)
            {
                root.Content = context => RenderChildren(context.Node, context.Html, context.Diagnostics);
            }

            return template;
        }

        return null;
    }

    /// <summary>选择器求值：目前只有 admonition:present/absent。</summary>
    private static bool SelectorMatches(string selector, MarkdownNode node) => selector switch
    {
        "admonition:present" => node.Attributes.ContainsKey("admonition"),
        "admonition:absent" => !node.Attributes.ContainsKey("admonition"),
        "none" => true,
        _ => false,
    };

    private PartTemplate BuildStructurePart(
        UserBlockPart part,
        bool isRoot,
        bool customKind = true,
        ElementTemplateVariant variant = ElementTemplateVariant.Default)
    {
        var template = CreateDeclaredPartTemplate(
            part.Part,
            part.Tag,
            part.Class,
            part.Style,
            part.Addressable,
            part.When,
            part.WhenSource,
            part.Repeat,
            part.RepeatMax,
            part.Generator,
            part.GeneratorParams,
            ResolveDeclaredPartRole(part, isRoot),
            // kind 类名与强调色变量仍挂在根上：kind 级样式/图标作用域不因换结构而失效。
            isRoot && customKind ? ResolveCustomBlockKindClass : null,
            isRoot && customKind ? node => ResolveSemanticAccentCss(node, "--cb-accent") : null,
            part.Content,
            part.Css,
            part.ClassRecipe,
            part.TagCondition,
            part.AttributesAfterIdentity,
            part.SourceCondition,
            part.ChildCollection,
            part.EmitWhenCollection,
            part.EmptyCollection,
            part.EmitNodeIdentity,
            part.SelfClosing,
            part.SuppressNodeStyle,
            part.EmitNodeStyle,
            part.EmitNone,
            part.Attributes,
            variant);

        // 引用值替换内容，因此引用部件是叶子；原文槽把块源文本转义写入；
        // 重复部件按当前项写内容。三者都由定义字段驱动，不按 kind/槽位名分派。
        if (part.Ref is not null)
        {
            var reference = part.Ref;
            template.Content = context => WriteReferenceValue(context, reference);
        }
        else if (part.Raw)
        {
            template.Content = WriteRawContent;
        }
        else if (part.Text is not null && UserBlockStructureRules.TryParseFieldTemplate(part.Text, out var textField))
        {
            template.Content = context => WriteDeclaredTextContent(context, textField);
        }
        else if (part.Repeat is not null && part.Content is null)
        {
            template.Content = WriteRepeatEntryContent;
        }

        // 受控 URL 绑定：只把定义里的 {field} 翻译成“已声明字段”来源，
        // 属性名固定为 href/src，值在渲染期按解码后的协议白名单校验并走属性转义。
        if (part.Href is not null && UserBlockStructureRules.TryParseFieldTemplate(part.Href, out var hrefField))
        {
            template.Attributes.Add(new PartAttributeTemplate(
                UserBlockStructureRules.HrefAttribute,
                PartAttributeValueSource.DeclaredFieldUrl,
                hrefField));
        }

        if (part.Src is not null && UserBlockStructureRules.TryParseFieldTemplate(part.Src, out var srcField))
        {
            template.Attributes.Add(new PartAttributeTemplate(
                UserBlockStructureRules.SrcAttribute,
                PartAttributeValueSource.DeclaredFieldUrl,
                srcField));
            template.SelfClosing = true;
        }

        foreach (var child in part.Children)
        {
            template.Children.Add(BuildStructurePart(child, isRoot: false, variant: variant));
        }

        return template;
    }

    /// <summary>
    /// v2 字段的唯一构造入口：定义声明路径与图结构路径都走这里，
    /// 保证两边的标签插值、条件类名、when、repeat、generator 语义逐字一致。
    /// </summary>
    private PartTemplate CreateDeclaredPartTemplate(
        string part,
        string? tag,
        string? rawClass,
        string? style,
        bool addressable,
        string? when,
        string? whenSource,
        string? repeat,
        int repeatMax,
        string? generator,
        string? generatorParams,
        ElementPartRole role,
        Func<MarkdownNode, string?>? extraClass,
        Func<MarkdownNode, string?>? extraCss,
        string? content = null,
        string? css = null,
        string? classRecipe = null,
        string? tagCondition = null,
        bool attributesAfterIdentity = false,
        string? sourceCondition = null,
        string? childCollection = null,
        string? emitWhenCollection = null,
        string? emptyCollection = null,
        bool emitNodeIdentity = true,
        bool selfClosing = false,
        bool suppressNodeStyle = false,
        bool emitNodeStyle = false,
        bool emitNone = false,
        IReadOnlyList<UserBlockPartAttribute>? attributes = null,
        ElementTemplateVariant variant = ElementTemplateVariant.Default)
    {
        var isTagExpression = UserBlockStructureRules.IsTagExpression(tag);
        var resolvedExtraCss = ComposeExtraCss(extraCss, css);
        var resolvedExtraClass = ComposeExtraClass(extraClass, classRecipe, variant);
        var template = new PartTemplate
        {
            Part = part,
            // 标签插值在渲染期按块属性展开；展开不出来时按虚拟部件处理，
            // 绝不把 "{field}" 写进 HTML 标签位。
            Tag = isTagExpression ? null : tag,
            TagTemplate = isTagExpression ? node => ResolveDeclaredTag(tag!, node) : null,
            // 条件类名在渲染期求值；静态 token 同时保留在 Class 里供快照/绑定使用。
            Class = UserBlockStructureRules.StripConditionalClasses(rawClass),
            ConditionalClass = HasConditionalClassTokens(rawClass)
                ? node => ResolveDeclaredClass(rawClass, node)
                : null,
            Style = style,
            Addressable = addressable,
            EmitCondition = BuildWhenCondition(when),
            WhenSource = whenSource,
            RepeatCollection = repeat,
            RepeatMax = repeatMax,
            Generator = generator,
            GeneratorParams = generatorParams,
            Role = role,
            ExtraClass = resolvedExtraClass,
            ExtraCss = resolvedExtraCss,
            Emit = emitNone ? PartEmitMode.None : PartEmitMode.Emit,
            AttributesAfterIdentity = attributesAfterIdentity,
            SourceCondition = ResolveSourceCondition(sourceCondition),
            ChildCollection = childCollection,
            EmitWhenCollection = emitWhenCollection,
            EmptyCollectionHtml = emptyCollection is null ? null : ResolveEmptyCollection(emptyCollection),
        };

        ApplyDeclaredPrimitives(
            template,
            content,
            tagCondition,
            emitNodeIdentity,
            selfClosing,
            suppressNodeStyle,
            emitNodeStyle,
            attributes);
        return template;
    }

    /// <summary>来源子节点条件名 → 固定枚举；缺省 always（无约束）。</summary>
    private static PartSourceCondition ResolveSourceCondition(string? name) => name switch
    {
        "poem-title" => PartSourceCondition.PoemTitle,
        "poem-author" => PartSourceCondition.PoemAuthor,
        "poem-dynasty" => PartSourceCondition.PoemDynasty,
        "poem-form" => PartSourceCondition.PoemForm,
        "poem-source" => PartSourceCondition.PoemSource,
        "poem-note" => PartSourceCondition.PoemNote,
        "poem-header" => PartSourceCondition.PoemHeader,
        "poem-author-line" => PartSourceCondition.PoemAuthorLine,
        "poem-source-and-note" => PartSourceCondition.PoemSourceAndNote,
        _ => PartSourceCondition.Always,
    };

    /// <summary>空集合占位名 → 固定 HTML 片段；字面量只在代码里，定义只写名字。</summary>
    private static string? ResolveEmptyCollection(string name) => name switch
    {
        "timeline-empty" => "<div class=\"md-tl-empty\">在 @@ 行添加时间节点，空行后书写正文</div>",
        _ => null,
    };

    /// <summary>部件角色：定义声明优先，缺省按部件名推导。</summary>
    private ElementPartRole ResolveDeclaredPartRole(UserBlockPart part, bool isRoot)
    {
        if (isRoot)
        {
            return ElementPartRole.Root;
        }

        if (part.Role is not null
            && Enum.TryParse<ElementPartRole>(part.Role, ignoreCase: true, out var declared)
            && Enum.IsDefined(declared))
        {
            return declared;
        }

        return ResolveStructurePartRole(part.Part);
    }

    private Func<MarkdownNode, string?>? ComposeExtraCss(
        Func<MarkdownNode, string?>? existing,
        string? recipe)
    {
        var added = recipe is null ? null : ResolveCssRecipe(recipe);
        if (added is null)
        {
            return existing;
        }

        return existing is null ? added : node => existing(node) + added(node);
    }

    private Func<MarkdownNode, string?>? ComposeExtraClass(
        Func<MarkdownNode, string?>? existing,
        string? recipe,
        ElementTemplateVariant variant)
    {
        var added = recipe is null ? null : ResolveClassRecipe(recipe, variant);
        if (added is null)
        {
            return existing;
        }

        return existing is null
            ? added
            : node =>
            {
                var head = existing(node);
                var tail = added(node);
                return head is null || head.Length == 0
                    ? tail
                    : tail is null || tail.Length == 0
                        ? head
                        : head + " " + tail;
            };
    }

    /// <summary>
    /// schema v3 原语的唯一落地处：定义只写注册表里的名字与标志，
    /// 写入器/配方实现留在代码，两侧（定义路径与图路径）共用同一份映射。
    /// </summary>
    private void ApplyDeclaredPrimitives(
        PartTemplate template,
        string? content,
        string? tagCondition,
        bool emitNodeIdentity,
        bool selfClosing,
        bool suppressNodeStyle,
        bool emitNodeStyle,
        IReadOnlyList<UserBlockPartAttribute>? attributes)
    {
        if (content is not null && ResolveNamedContentWriter(content) is { } writer)
        {
            template.Content = writer;
        }

        if (string.Equals(tagCondition, "inline-style", StringComparison.Ordinal))
        {
            template.TagCondition = PartTagCondition.HasInlineStyle;
        }

        if (!emitNodeIdentity)
        {
            template.EmitNodeIdentity = false;
        }

        if (selfClosing)
        {
            template.SelfClosing = true;
        }

        if (suppressNodeStyle)
        {
            template.SuppressNodeStyle = true;
        }

        if (emitNodeStyle)
        {
            template.EmitNodeStyle = true;
        }

        if (attributes is { Count: > 0 })
        {
            foreach (var binding in attributes)
            {
                if (TryMapBoundAttribute(binding) is { } attributeTemplate)
                {
                    template.Attributes.Add(attributeTemplate);
                }
            }
        }
    }

    /// <summary>命名内容槽 → 写入器。名字是封闭集合，取值由结构校验保证。</summary>
    private PartWriter? ResolveNamedContentWriter(string name) => name switch
    {
        "inline" => WriteInlineContent,
        "text" => context => AppendEncoded(context.Html, context.Node.Text),
        "code-block" => WriteCodeBlockText,
        "tight-children" => context => RenderTightContainerChildren(context.Node, context.Html, context.Diagnostics),
        "children" => context => RenderChildren(context.Node, context.Html, context.Diagnostics),
        "link" => WriteLinkContent,
        "footnote-label" => WriteFootnoteLabel,
        "backref-icon" => context => context.Html.Append('↩'),
        "space" => context => context.Html.Append(' '),
        "task-list" => WriteTaskListItemContent,
        "nothing" => _ => { },
        "current-node-text" => WriteCurrentNodeText,
        "timeline-time" => WriteTimelineTime,
        "repeat-item" => static context => AppendEncoded(context.Html, context.RepeatItem ?? string.Empty),
        "poem-meta-break" => static context => context.Html.Append("<br class=\"poem-meta-break\"/>"),
        "admonition-icon" => WriteAdmonitionIcon,
        "admonition-title-text" => WriteAdmonitionTitleText,
        "custom-block-icon" => WriteCustomBlockIcon,
        _ => null,
    };

    /// <summary>命名内联样式配方 → 按块求值的 CSS 片段。</summary>
    private Func<MarkdownNode, string?>? ResolveCssRecipe(string name) => name switch
    {
        "align-block" => node => ResolveReplacedAlignmentCss(node, forceBlockDisplay: true),
        "align-inline" => node => ResolveReplacedAlignmentCss(node, forceBlockDisplay: false),
        "table-row-vars" => BuildTableRowCascadeVars,
        "cell-align" => node =>
            node.Attributes.TryGetValue("column-align", out var align) && !string.IsNullOrEmpty(align)
                ? "text-align: " + align + ";"
                : null,
        "math-vars" => BuildMathStyleVars,
        "matrix-vars" => node => BuildMatrixExtraCss(node, CountMatrixColumns(CollectMatrixRows(node))),
        "accent-adm" => node => ResolveSemanticAccentCss(node, "--adm-accent"),
        "accent-cb" => node => ResolveSemanticAccentCss(node, "--cb-accent"),
        "timeline-vars" => BuildTimelineStyleVars,
        _ => null,
    };

    /// <summary>命名条件类名配方 → 按块求值的类名片段；需要档位的配方按 <paramref name="variant"/> 求值。</summary>
    private Func<MarkdownNode, string?>? ResolveClassRecipe(string name, ElementTemplateVariant variant) => name switch
    {
        "language" => ResolveLanguageClass,
        "kind" => ResolveCustomBlockKindClass,
        "mermaid-state" => node => node.Attributes.ContainsKey(MarkdownAttributes.Unclosed) ? "mermaid-pending" : null,
        "matrix-state" => node => node.Attributes.ContainsKey(MarkdownAttributes.Unclosed) ? "image-matrix-pending" : null,
        "qrcode-state" => ResolveQrCodeStateClass,
        "radar-state" => ResolveRadarStateClass,
        "poem-state" => node => node.Attributes.ContainsKey(MarkdownAttributes.Unclosed) ? "poem-pending" : null,
        "timeline-time-state" => node => string.IsNullOrEmpty(GetAttribute(node, "timeline-time"))
            ? "md-tl-time-empty"
            : null,
        "admonition-kind" => node => ResolveAdmonitionKind(
            node.Attributes.GetValueOrDefault("admonition") ?? string.Empty),
        "kind-minimal" => node => ResolveCustomBlockKindClass(node) + " custom-block-minimal",
        "timeline-layout" => node => BuildTimelineVariantClass(node, variant),
        _ => null,
    };

    /// <summary>显式属性绑定 → 属性模板。取值器是封闭枚举，名字与来源已在结构校验里过白名单。</summary>
    private static PartAttributeTemplate? TryMapBoundAttribute(UserBlockPartAttribute binding)
    {
        var (source, value) = binding.Source switch
        {
            "node-text" => (PartAttributeValueSource.NodeText, (string?)null),
            "node:anchor" => (PartAttributeValueSource.NodeAttribute, "anchor"),
            "node:id" => (PartAttributeValueSource.NodeAttribute, "id"),
            "node:href:backlink" => (PartAttributeValueSource.NodeAttribute, "href"),
            "node:timeline-side-effective" => (PartAttributeValueSource.NodeAttribute, "timeline-side-effective"),
            "node:url:link" => (PartAttributeValueSource.DeclaredFieldUrl, "url"),
            // 图片走内置的 image 策略（允许 data:image），与用户定义的元素绑定同一口径。
            "node:url:image" => (PartAttributeValueSource.NodeAttribute, "url"),
            "ref:target" => (PartAttributeValueSource.RefTarget, null),
            "ref:backref-href" => (PartAttributeValueSource.RefBackrefHref, null),
            "ref:backref-item" => (PartAttributeValueSource.RefBackrefItem, null),
            "ref:backref-title" => (PartAttributeValueSource.RefBackrefTitle, null),
            "ref:backref-aria-label" => (PartAttributeValueSource.RefBackrefAriaLabel, null),
            _ => (PartAttributeValueSource.Constant, (string?)null),
        };

        var literal = binding.Source.StartsWith("literal:", StringComparison.Ordinal)
            ? binding.Source["literal:".Length..]
            : value;

        // 受控 URL 绑定必须带上对应的安全策略，否则 image 绑定会退化成受限的通用 URL 校验。
        var policy = binding.Source switch
        {
            "node:url:link" => "link",
            "node:url:image" => "image",
            "node:href:backlink" => "backlink",
            "ref:backref-href" => "backlink",
            _ => null,
        };

        return new PartAttributeTemplate(binding.Name, source, literal, policy);
    }

    /// <summary>
    /// 按节点已声明字段求值标签插值；字段缺失或展开后不在标签白名单内时返回 null（按虚拟部件处理）。
    /// </summary>
    private static string? ResolveDeclaredTag(string tag, MarkdownNode node)
    {
        if (!UserBlockStructureRules.TryGetTagField(tag, out var field))
        {
            return null;
        }

        var value = ResolveDeclaredField(node, field);
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        UserBlockStructureRules.TryGetTagPrefix(tag, out var prefix);
        var expanded = prefix + value;
        return UserBlockStructureRules.TryNormalizeTag(expanded, out var normalized)
               && !UserBlockStructureRules.IsTagExpression(normalized)
            ? normalized
            : null;
    }

    /// <summary>
    /// 只读字段求值：先取节点已声明的属性（定义声明的枚举字段），
    /// 再回落到内置派生字段。两条来源都不成立时返回 null，绝不猜测。
    /// </summary>
    private static string? ResolveDeclaredField(MarkdownNode node, string field)
    {
        if (node.Attributes.TryGetValue(field, out var declared)
            && !string.IsNullOrWhiteSpace(declared))
        {
            return declared.Trim();
        }

        foreach (var pair in node.Attributes)
        {
            if (string.Equals(pair.Key, field, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value.Trim();
            }
        }

        return ResolveDerivedField(node, field);
    }

    /// <summary>部件条件：只做“字段值是否等于声明值”的相等判断，字段缺失视为不成立。</summary>
    private static Func<MarkdownNode, bool>? BuildWhenCondition(string? when)
    {
        if (when is null || !UserBlockStructureRules.TryParseWhen(when, out var field, out var expected))
        {
            return null;
        }

        return node => string.Equals(
            ResolveDeclaredField(node, field),
            expected,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasConditionalClassTokens(string? className)
    {
        foreach (var token in UserBlockStructureRules.EnumerateClassTokens(className))
        {
            if (UserBlockStructureRules.TrySplitConditionalClassToken(token, out _, out var condition)
                && condition.Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 条件类名求值：按声明顺序输出无条件 token 与命中条件的 token；
    /// 条件不成立时该 token 整条跳过，绝不把条件文法写进 class 字节。
    /// </summary>
    private static string? ResolveDeclaredClass(string? className, MarkdownNode node)
    {
        var tokens = new List<string>();
        foreach (var token in UserBlockStructureRules.EnumerateClassTokens(className))
        {
            if (!UserBlockStructureRules.TrySplitConditionalClassToken(token, out var name, out var condition)
                || condition.Length == 0)
            {
                tokens.Add(token);
                continue;
            }

            if (UserBlockStructureRules.TryParseWhen(condition, out var field, out var expected)
                && string.Equals(
                    ResolveDeclaredField(node, field),
                    expected,
                    StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add(name);
            }
        }

        return tokens.Count == 0 ? null : string.Join(' ', tokens);
    }

    /// <summary>重复项内容：token 项写转义文本，节点项按节点自身模板渲染。</summary>
    private void WriteRepeatEntryContent(TemplateRenderContext context)
    {
        if (context.RepeatItem is { } token)
        {
            context.Html.Append(WebUtility.HtmlEncode(token));
            return;
        }

        if (context.CurrentNode is { } item && !ReferenceEquals(item, context.Node))
        {
            RenderNode(item, context.Html, context.Diagnostics);
        }
    }

    /// <summary>
    /// 原文槽内容：取与 <c>generator</c> 相同的块源文本，<b>整段转义</b>后写入。
    /// 空或纯空白时不写任何内容（部件本身仍发射）；绝不把源文本当 HTML 解码。
    /// </summary>
    private static void WriteRawContent(TemplateRenderContext context)
    {
        var source = ResolveGeneratorSource(context.CurrentNode);
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        context.Html.Append(WebUtility.HtmlEncode(source));
    }

    /// <summary>
    /// 槽位文本内容：取块上<b>已声明属性</b>的值（精确匹配优先，其次忽略大小写），
    /// 转义后写入；字段未声明、值为空或纯空白时零内容（与 <c>raw</c> 同一口径，
    /// 避免纯空白在行内布局里显形）。与 <c>raw</c> 的区别只是数据源为实例字段。
    /// </summary>
    private static void WriteDeclaredTextContent(TemplateRenderContext context, string field)
    {
        var value = ResolveDeclaredFieldValue(context.CurrentNode, field);
        if (!string.IsNullOrWhiteSpace(value))
        {
            context.Html.Append(WebUtility.HtmlEncode(value));
        }
    }

    private static string? ResolveDeclaredFieldValue(MarkdownNode? node, string field)
    {
        if (node is null)
        {
            return null;
        }

        foreach (var pair in node.Attributes)
        {
            if (string.Equals(pair.Key, field, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        foreach (var pair in node.Attributes)
        {
            if (string.Equals(pair.Key, field, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>引用值内容：只写派生值本身（转义文本），不写锚点/href，也不改部件属性。</summary>
    private void WriteReferenceValue(TemplateRenderContext context, string reference)
    {
        if (CurrentDocumentRefIndex()?.Find(context.Node) is not { } entry)
        {
            return;
        }

        var value = reference switch
        {
            "slug" => entry.Slug,
            "ordinal" => entry.Ordinal.ToString(CultureInfo.InvariantCulture),
            "backrefs" => entry.Backrefs,
            _ => null,
        };

        if (!string.IsNullOrEmpty(value))
        {
            context.Html.Append(WebUtility.HtmlEncode(value));
        }
    }

    /// <summary>槽位 token → 部件角色；角色只决定“是否写得进节点属性/样式”，不改变填充器。</summary>
    private static ElementPartRole ResolveStructurePartRole(string part) => part switch
    {
        PartTokens.Icon => ElementPartRole.Icon,
        PartTokens.Title => ElementPartRole.Header,
        PartTokens.TitleText => ElementPartRole.Title,
        PartTokens.Body or PartTokens.Content => ElementPartRole.Content,
        PartTokens.Footer => ElementPartRole.Tail,
        PartTokens.Badge or PartTokens.Meta => ElementPartRole.Meta,
        _ => ElementPartRole.Wrapper,
    };

    /// <summary>
    /// 标题槽位的填充器：写节点 <c>title</c> 属性文本（已转义）。
    /// 没有标题槽位的结构不会调用到这里——标题不会被塞进正文或图标位。
    /// </summary>
    private static void WriteCustomBlockTitleText(TemplateRenderContext context)
    {
        if (context.Node.Attributes.TryGetValue(CustomTitleAttribute, out var title)
            && !string.IsNullOrWhiteSpace(title))
        {
            context.Html.Append(WebUtility.HtmlEncode(title.Trim()));
        }
    }

    /// <summary>用户元素定义索引：键为定义 Id（即节点 custom-kind），重复 Id 保留首条。</summary>
    private static IReadOnlyDictionary<string, UserBlockDefinition> BuildUserBlockIndex(
        IReadOnlyList<UserBlockDefinition>? definitions)
    {
        var index = new Dictionary<string, UserBlockDefinition>(StringComparer.Ordinal);
        if (definitions is null)
        {
            return index;
        }

        foreach (var definition in definitions)
        {
            index.TryAdd(definition.Id, definition);
        }

        return index;
    }

    /// <summary>当前渲染的整篇文档的引用索引；首次使用时建立，渲染结束随文档状态一起丢弃。</summary>
    private DocumentRefIndex? CurrentDocumentRefIndex()
    {
        if (_activeDocument is null)
        {
            return null;
        }

        return _documentRefIndex ??= DocumentRefIndex.Build(_activeDocument.Root);
    }

    /// <summary>节点的结构声明是否使用引用字段；用于让引用派生值参与块签名。</summary>
    private bool NodeStructureUsesRef(MarkdownNode node)
    {
        if (_activeUserBlocks is null)
        {
            return false;
        }

        var kind = ElementKind.NormalizeToken(ElementKind.Resolve(node));
        if (kind is null || !_activeUserBlocks.TryGetValue(kind, out var definition))
        {
            return false;
        }

        if (_structureUsesRef is null)
        {
            _structureUsesRef = new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        if (!_structureUsesRef.TryGetValue(kind, out var usesRef))
        {
            // 声明与结构图都算：画布可能只改图常量，不回写结构声明。
            usesRef = StructureUsesRef(definition.Structure.Root)
                || GraphStructureUsesRef(definition.StructureGraph);
            _structureUsesRef[kind] = usesRef;
        }

        return usesRef;
    }

    private static bool StructureUsesRef(UserBlockPart? part) =>
        part is not null
        && (part.Ref is not null || part.Children.Any(StructureUsesRef));

    /// <summary>结构图常量里的部件树是否使用 ref；没有结构常量时视为不使用。</summary>
    private static bool GraphStructureUsesRef(NodeGraph? graph)
    {
        var constant = graph?
            .FindNode(NodeGraphMigration.TemplateNodeId)?
            .FindInput(ElementDefinitionGraphCodec.StructurePort)?
            .Constant;
        if (constant is not { Kind: GraphValueKind.PartTree } || string.IsNullOrEmpty(constant.Raw))
        {
            return false;
        }

        var value = UserBlockStructureGraph.ToValue(constant.Raw);
        return !value.IsNone && PartsUseRef(value.AsParts());
    }

    private static bool PartsUseRef(IReadOnlyList<GraphPart> parts) =>
        parts.Any(part => part.Ref is not null || PartsUseRef(part.Children));

    /// <summary>
    /// 文档级引用索引：为每个自定义节点记录同 kind 的出现序号、slug 与回链文本。
    /// 建立范围是整篇文档（按文档顺序一次扫描），因此跨块引用在任意块渲染时都可读；
    /// 渲染结束即丢弃，下一次渲染重建——文档树变化后不会残留旧序号。
    /// </summary>
    private sealed class DocumentRefIndex
    {
        private readonly Dictionary<string, Entry> _byNodeId = new(StringComparer.Ordinal);

        public static DocumentRefIndex Build(MarkdownNode root)
        {
            var index = new DocumentRefIndex();
            var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var node in root.Descendants())
            {
                if (node.Type != NodeType.Custom)
                {
                    continue;
                }

                var kind = ElementKind.NormalizeToken(ElementKind.Resolve(node)) ?? "custom";
                ordinals.TryGetValue(kind, out var ordinal);
                ordinal++;
                ordinals[kind] = ordinal;

                var slug = GetAttribute(node, "slug");
                if (string.IsNullOrWhiteSpace(slug))
                {
                    slug = "custom-" + kind + "-" + ordinal.ToString(CultureInfo.InvariantCulture);
                }

                var backrefs = GetAttribute(node, "backrefs");
                index._byNodeId[node.Id] = new Entry(
                    ordinal,
                    SanitizeRefToken(slug),
                    string.Join(", ", SplitRefTokens(backrefs)));
            }

            return index;
        }

        public Entry? Find(MarkdownNode node) =>
            _byNodeId.TryGetValue(node.Id, out var entry) ? entry : null;

        private static IEnumerable<string> SplitRefTokens(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                yield break;
            }

            foreach (var token in raw.Split(','))
            {
                var value = token.Trim();
                if (value.Length > 0)
                {
                    yield return value;
                }
            }
        }

        /// <summary>slug 只作为文本内容输出，按标识符字符面净化，避免空白与尖括号混入。</summary>
        private static string SanitizeRefToken(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var c in value.Trim())
            {
                builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
            }

            return builder.Length == 0 ? "custom" : builder.ToString();
        }

        public readonly record struct Entry(int Ordinal, string Slug, string Backrefs);
    }

    private const string CustomTitleAttribute = "title";

    /// <summary>
    /// 按“类型 + kind”取类型级定义：带 kind 的定义优先，其次同类型未限定定义。
    /// 渲染器、检查器与指纹共用这一处优先级，不许各自 FirstOrDefault。
    /// </summary>
    public static StyleDefinition? ResolveTypeDefinition(StyleSheet sheet, NodeType type, string? kind)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var index = TypeDefinitionIndex.Build(sheet);
        return index.TryGet(type, kind, out var definition) ? definition : null;
    }

    /// <summary>
    /// 类型定义索引：kind 限定的定义优先于同类型不限 kind 的定义，
    /// 与 <see cref="StyleResolver"/> 的覆盖顺序一致。
    /// </summary>
    private sealed class TypeDefinitionIndex
    {
        private readonly Dictionary<NodeType, StyleDefinition> _byType = new();
        private readonly Dictionary<(NodeType, string), StyleDefinition> _byKind =
            new(ElementKind.KeyComparer);

        public static TypeDefinitionIndex Build(StyleSheet sheet)
        {
            var index = new TypeDefinitionIndex();
            foreach (var definition in sheet.Styles)
            {
                if (string.Equals(definition.Id, "default", StringComparison.Ordinal)
                    || !definition.AppliesTo.HasValue)
                {
                    continue;
                }

                var type = definition.AppliesTo.Value;
                if (string.IsNullOrWhiteSpace(definition.AppliesToKind))
                {
                    index._byType.TryAdd(type, definition);
                }
                else
                {
                    index._byKind.TryAdd((type, definition.AppliesToKind.Trim()), definition);
                }
            }

            return index;
        }

        public bool TryGet(NodeType type, string? kind, out StyleDefinition definition)
        {
            if (!string.IsNullOrWhiteSpace(kind)
                && _byKind.TryGetValue((type, kind.Trim()), out definition!))
            {
                return true;
            }

            return _byType.TryGetValue(type, out definition!);
        }
    }

    private void RenderPart(
        TemplateRenderContext context,
        PartTemplate part,
        ElementTemplate template)
    {
        // P7：定义显式声明不发射——本部件及其子树不产出任何字节。
        if (part.Emit == PartEmitMode.None)
        {
            return;
        }

        // 定义里的部件条件：只决定是否发射本部件及其子树，不改变结构形态。
        // 条件读整块的已声明字段（context.Node），repeat 展开时也按块取值。
        if (part.EmitCondition is not null && !part.EmitCondition(context.Node))
        {
            return;
        }

        // 源数据条件：固定枚举，按当前节点的解析结果判定；不命中时本部件及子树零发射。
        if (part.WhenSource is not null && !MatchesWhenSource(part.WhenSource, context.CurrentNode))
        {
            return;
        }

        if (part.RepeatCollection is not null)
        {
            RenderRepeatedPart(context, part, template);
            return;
        }

        RenderPartCore(context, part, template);
    }

    /// <summary>P7：找出第一个“既声明标签又声明不发射”的部件；没有则返回 null。</summary>
    private static string? FindEmitConflict(PartTemplate part)
    {
        if (part.Emit == PartEmitMode.None && part.Tag is not null)
        {
            return part.Part + "（" + part.Tag + "）";
        }

        foreach (var child in part.Children)
        {
            if (FindEmitConflict(child) is { } conflict)
            {
                return conflict;
            }
        }

        return null;
    }

    /// <summary>
    /// 按固定派生集合展开部件。集合名只能是枚举里的名字；上限越界直接拒绝（不做截断）。
    /// 嵌套 repeat 只在声明了 <see cref="ElementStructureKey.Poem"/> 的模板内、
    /// 且严格为“外层 stanzas → 内层 lines”两级时放行；第三级与其它组合一律拒绝。
    /// </summary>
    private void RenderRepeatedPart(
        TemplateRenderContext context,
        PartTemplate part,
        ElementTemplate template)
    {
        if (context.RepeatDepth > 0)
        {
            if (template.StructureKey != ElementStructureKey.Poem
                || context.RepeatDepth >= 2)
            {
                context.Diagnostics.Add(new DiagnosticMessage
                {
                    Severity = DiagnosticSeverity.Error,
                    Code = context.RepeatDepth >= 2 ? "ELEMENT_REPEAT_DEPTH_EXCEEDED" : "ELEMENT_REPEAT_NESTED",
                    Message = template.StructureKey == ElementStructureKey.Poem
                        ? "repeat 只允许两级（外层 stanzas、内层 lines）；已跳过：" + part.Part
                        : "非诗体结构不允许嵌套 repeat；已跳过：" + part.Part,
                });
                return;
            }

            if (!string.Equals(context.RepeatCollection, "stanzas", StringComparison.Ordinal)
                || !string.Equals(part.RepeatCollection, "lines", StringComparison.Ordinal))
            {
                context.Diagnostics.Add(new DiagnosticMessage
                {
                    Severity = DiagnosticSeverity.Error,
                    Code = "ELEMENT_REPEAT_NESTED",
                    Message = "诗体只允许“外层 stanzas → 内层 lines”这一固定配对；已跳过：" + part.Part,
                });
                return;
            }
        }

        if (part.RepeatMax is < 1 or > 64)
        {
            context.Diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Error,
                Code = "ELEMENT_REPEAT_MAX_INVALID",
                Message = "repeat 上限必须是 1..64；已拒绝：" + part.Part,
            });
            return;
        }

        var entries = ResolveRepeatEntries(context.CurrentNode, part.RepeatCollection!);
        if (entries is null)
        {
            context.Diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Error,
                Code = "ELEMENT_REPEAT_COLLECTION_UNSUPPORTED",
                Message = "不支持在渲染期消费的派生集合：" + part.RepeatCollection,
            });
            return;
        }

        if (entries.Count > part.RepeatMax)
        {
            context.Diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Error,
                Code = "ELEMENT_REPEAT_LIMIT_EXCEEDED",
                Message = $"repeat 展开项数 {entries.Count} 超过上限 {part.RepeatMax}；已拒绝而非截断：" + part.Part,
            });
            return;
        }

        if (context.RepeatDepth == 0
            && CountRepeatExpansion(part, context.CurrentNode) > MaxRepeatExpansion)
        {
            context.Diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Error,
                Code = "ELEMENT_REPEAT_EXPANSION_EXCEEDED",
                Message = $"重复总展开超过 {MaxRepeatExpansion} 项；已拒绝而非截断：" + part.Part,
            });
            return;
        }

        var previousItem = context.RepeatItem;
        var previousOrdinal = context.RepeatOrdinal;
        var previousNode = context.CurrentNode;
        var previousCollection = context.RepeatCollection;

        try
        {
            context.RepeatCollection = part.RepeatCollection;
            for (var index = 0; index < entries.Count; index++)
            {
                context.RepeatItem = entries[index].Token;
                context.RepeatOrdinal = index + 1;
                if (entries[index].Node is { } itemNode)
                {
                    context.CurrentNode = itemNode;
                }

                context.RepeatDepth++;
                try
                {
                    RenderPartCore(context, part, template);
                }
                finally
                {
                    context.RepeatDepth--;
                }
            }
        }
        finally
        {
            context.RepeatItem = previousItem;
            context.RepeatOrdinal = previousOrdinal;
            context.CurrentNode = previousNode;
            context.RepeatCollection = previousCollection;
        }
    }

    /// <summary>预检：本次节点上该部件子树会展开的总项数；未知集合返回超限值。</summary>
    private static long CountRepeatExpansion(PartTemplate part, MarkdownNode node)
    {
        if (part.RepeatCollection is null)
        {
            long nested = 0;
            foreach (var child in part.Children)
            {
                nested += CountRepeatExpansion(child, node);
            }

            return nested;
        }

        var entries = ResolveRepeatEntries(node, part.RepeatCollection);
        if (entries is null || entries.Count > part.RepeatMax)
        {
            return long.MaxValue;
        }

        long total = entries.Count;
        foreach (var entry in entries)
        {
            var itemNode = entry.Node ?? node;
            foreach (var child in part.Children)
            {
                total += CountRepeatExpansion(child, itemNode);
            }
        }

        return total;
    }

    /// <summary>repeat 展开项：token 集合只带字符串，节点集合带节点（样式与文本随节点走）。</summary>
    private readonly record struct RepeatEntry(MarkdownNode? Node, string? Token);

    /// <summary>
    /// 派生集合只开放已声明且语义确定的项；未知集合返回 null 由上层报诊断。
    /// 集合名是固定枚举，成员筛选由集合名而不是渲染期类型分支决定。
    /// </summary>
    private static List<RepeatEntry>? ResolveRepeatEntries(MarkdownNode node, string collection)
    {
        var entries = new List<RepeatEntry>();

        switch (collection)
        {
            case "backrefs":
            {
                var raw = GetAttribute(node, "backrefs");
                if (string.IsNullOrEmpty(raw))
                {
                    return entries;
                }

                foreach (var token in raw.Split(','))
                {
                    var item = token.Trim();
                    if (item.Length > 0)
                    {
                        entries.Add(new RepeatEntry(null, item));
                    }
                }

                return entries;
            }

            case "timeline-tags":
            {
                foreach (var tag in TimelineMarker.SplitTags(
                             node.Attributes.TryGetValue("timeline-tags", out var raw) ? raw : null))
                {
                    entries.Add(new RepeatEntry(null, tag));
                }

                return entries;
            }

            case "stanzas":
                AddNodeEntries(node, NodeType.PoemStanza, entries);
                // 非诗体结构没有诗节节点：回落到“直属块级子节点”，
                // 这是结构位置规则，不按元素类型名分派。
                if (entries.Count == 0)
                {
                    AddBlockChildEntries(node, entries);
                }

                return entries;

            case "lines":
                AddNodeEntries(node, NodeType.PoemLine, entries);
                if (entries.Count == 0)
                {
                    AddBlockChildEntries(node, entries);
                }

                return entries;

            case "images":
                AddDescendantEntries(node, NodeType.Image, entries);
                return entries;

            // 通用结构来源：直属块级子节点。不按元素类型名筛选，只看节点分类。
            case "children":
                AddBlockChildEntries(node, entries);
                return entries;

            // 通用结构来源：块源文本按非空行切分，每行一个转义文本项。
            case "body-lines":
            {
                foreach (var line in ResolveGeneratorSource(node).Split('\n'))
                {
                    var item = line.TrimEnd('\r');
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        entries.Add(new RepeatEntry(null, item));
                    }
                }

                return entries;
            }

            default:
                return null;
        }
    }

    private static void AddNodeEntries(MarkdownNode node, NodeType type, List<RepeatEntry> entries)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == type)
            {
                entries.Add(new RepeatEntry(child, null));
            }
        }
    }

    private static void AddDescendantEntries(MarkdownNode node, NodeType type, List<RepeatEntry> entries)
    {
        foreach (var descendant in node.Descendants())
        {
            if (descendant.Type == type)
            {
                entries.Add(new RepeatEntry(descendant, null));
            }
        }
    }

    /// <summary>直属块级子节点：只按节点分类筛选，不识别具体元素类型名。</summary>
    private static void AddBlockChildEntries(MarkdownNode node, List<RepeatEntry> entries)
    {
        foreach (var child in node.Children)
        {
            if (child.Category is NodeCategory.Block or NodeCategory.Container)
            {
                entries.Add(new RepeatEntry(child, null));
            }
        }
    }

    /// <summary>
    /// 源数据条件求值：只读解析结果，不读字段、不做类型名分派。
    /// 未知取值一律判不命中（零发射），绝不因为“没看懂”就默认发射。
    /// </summary>
    private static bool MatchesWhenSource(string condition, MarkdownNode node) => condition switch
    {
        "body-empty" => string.IsNullOrWhiteSpace(ResolveGeneratorSource(node)),
        "body-any" => !string.IsNullOrWhiteSpace(ResolveGeneratorSource(node)),
        "children-empty" => !HasBlockChildren(node),
        "children-any" => HasBlockChildren(node),
        "images-any" => ResolveRepeatEntries(node, "images") is { Count: > 0 },
        _ => false,
    };

    /// <summary>是否有直属块级子节点：只看节点分类，不看元素类型名。</summary>
    private static bool HasBlockChildren(MarkdownNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.Category is NodeCategory.Block or NodeCategory.Container)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 部件发射前检查：来源子节点条件与集合存在条件只决定“是否发射这个部件及其子树”，
    /// 不改变结构形态。命中单个来源节点时把当前节点切换为该来源节点。
    /// </summary>
    private static bool ShouldEmitPart(PartTemplate part, TemplateRenderContext context)
    {
        if (part.EmitWhenCollection is { } collection
            && ResolveRepeatEntries(context.CurrentNode, collection) is { Count: 0 })
        {
            return false;
        }

        switch (part.SourceCondition)
        {
            case PartSourceCondition.Always:
                return true;
            case PartSourceCondition.PoemHeader:
                return HasAnyChild(
                    context.Node,
                    NodeType.PoemTitle,
                    NodeType.PoemAuthor,
                    NodeType.PoemDynasty,
                    NodeType.PoemForm);
            case PartSourceCondition.PoemAuthorLine:
                return HasAnyChild(context.Node, NodeType.PoemAuthor, NodeType.PoemDynasty);
            case PartSourceCondition.PoemSourceAndNote:
                return FindChild(context.Node, NodeType.PoemSource) is not null
                       && FindChild(context.Node, NodeType.PoemNote) is not null;
        }

        var sourceType = part.SourceCondition switch
        {
            PartSourceCondition.PoemTitle => NodeType.PoemTitle,
            PartSourceCondition.PoemAuthor => NodeType.PoemAuthor,
            PartSourceCondition.PoemDynasty => NodeType.PoemDynasty,
            PartSourceCondition.PoemForm => NodeType.PoemForm,
            PartSourceCondition.PoemSource => NodeType.PoemSource,
            PartSourceCondition.PoemNote => NodeType.PoemNote,
            _ => (NodeType?)null,
        };

        if (sourceType is null)
        {
            return true;
        }

        var source = FindChild(context.Node, sourceType.Value);
        if (source is null)
        {
            return false;
        }

        context.CurrentNode = source;
        return true;
    }

    private static MarkdownNode? FindChild(MarkdownNode node, NodeType type)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == type)
            {
                return child;
            }
        }

        return null;
    }

    private static bool HasAnyChild(MarkdownNode node, params NodeType[] types)
    {
        foreach (var child in node.Children)
        {
            foreach (var type in types)
            {
                if (child.Type == type)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void RenderPartCore(
        TemplateRenderContext context,
        PartTemplate part,
        ElementTemplate template)
    {
        var previousNode = context.CurrentNode;
        if (!ShouldEmitPart(part, context))
        {
            return;
        }

        try
        {
            RenderPartBody(context, part, template);
        }
        finally
        {
            context.CurrentNode = previousNode;
        }
    }

    private void RenderPartBody(
        TemplateRenderContext context,
        PartTemplate part,
        ElementTemplate template)
    {
        var generator = ResolvePartGenerator(context, part);

        // 标签插值：定义驱动的部件在渲染期求值；求不出来即按虚拟部件处理。
        var declaredTag = part.Tag ?? part.TagTemplate?.Invoke(context.CurrentNode);
        if (declaredTag is null)
        {
            if (part.ChildCollection is not null)
            {
                RenderChildCollection(context, part);
                return;
            }

            if (part.Content is not null)
            {
                part.Content(context);
            }
            else if (template.Slots.TryGetValue(part.Part, out var virtualWriter))
            {
                virtualWriter(context);
            }

            foreach (var child in part.Children)
            {
                RenderPart(context, child, template);
            }

            return;
        }

        var html = context.Html;
        var attributes = BuildPartAttributes(context, part);
        if (attributes is null)
        {
            // 图片 URL 不安全或缺失：整个部件不发射，绝不输出没有 src 的 <img>。
            return;
        }

        // 标签模板按节点派生字段展开（h{level}）；派生不成立时原样使用描述标签。
        var tag = ResolveDescriptionTag(declaredTag, context.CurrentNode) ?? declaredTag;

        // 条件标签只决定“是否发射外层标签”：不成立时只写内容，不做结构切换。
        if (!ShouldEmitPartTag(part.TagCondition, context.CurrentNode))
        {
            RenderPartContentOnly(context, part, template);
            return;
        }

        html.Append('<').Append(tag);

        var bindings = ResolvePartBindings(context, template.NodeType, part.Part);
        var bindingClasses = BuildBindingClassTokens(bindings);
        var bindingCss = BuildBindingCss(bindings);

        var extraClass = part.ExtraClass?.Invoke(context.CurrentNode);
        // 条件类名与标题类名一样按整块取值（repeat 内不按当前项误读条件字段）。
        var classValue = part.ConditionalClass is not null
            ? part.ConditionalClass(context.Node)
            : part.Class;
        if (!string.IsNullOrEmpty(classValue)
            || !string.IsNullOrEmpty(extraClass)
            || !string.IsNullOrEmpty(bindingClasses))
        {
            html.Append(" class=\"").Append(classValue);
            if (!string.IsNullOrEmpty(extraClass))
            {
                if (!string.IsNullOrEmpty(classValue))
                {
                    html.Append(' ');
                }

                html.Append(extraClass);
            }

            if (!string.IsNullOrEmpty(bindingClasses))
            {
                if (!string.IsNullOrEmpty(classValue) || !string.IsNullOrEmpty(extraClass))
                {
                    html.Append(' ');
                }

                html.Append(bindingClasses);
            }

            html.Append('"');
        }

        if (!part.AttributesAfterIdentity)
        {
            AppendPartAttributes(attributes, html);
        }

        if (part.Addressable)
        {
            html.Append(" data-part=\"").Append(part.Part).Append('"');
        }

        AppendBindingDataAttributes(bindings, html);

        if (part.Role == ElementPartRole.Root)
        {
            if (!part.EmitNodeIdentity)
            {
                // 直出分支对这类根不写节点身份与实例样式（br / 脚注组），模板保持同一字节形态。
                if (!string.IsNullOrEmpty(part.Style) && !_fragmentOptions.SuppressInlineStyle)
                {
                    html.Append(" style=\"").Append(WebUtility.HtmlEncode(part.Style)).Append('"');
                }
            }
            else
            {
                AppendNodeAttributes(context.CurrentNode, html);

                if (part.SuppressNodeStyle)
                {
                    // 该根只承担节点身份，实例样式由内部受控元素输出。
                }
                else
                {
                    var extraCss = part.ExtraCss?.Invoke(context.CurrentNode);
                    var tail = string.IsNullOrEmpty(bindingCss)
                        ? extraCss
                        : string.IsNullOrEmpty(extraCss) ? bindingCss : extraCss + " " + bindingCss;

                    if (string.IsNullOrEmpty(part.Style))
                    {
                        AppendStyle(context.CurrentNode, html, tail);
                    }
                    else if (!_fragmentOptions.SuppressInlineStyle)
                    {
                        // 元素定义里的布局是基底：实例样式（节点样式 / 强调色变量）排在后面，可覆盖它。
                        var merged = MergeCss(part.Style, BuildNodeInlineCss(context.CurrentNode, tail));
                        if (!string.IsNullOrWhiteSpace(merged))
                        {
                            html.Append(" style=\"").Append(WebUtility.HtmlEncode(merged)).Append('"');
                        }
                    }
                }
            }
        }
        else
        {
            if (!_fragmentOptions.SuppressInlineStyle)
            {
                var merged = MergeCss(part.Style, bindingCss);
                if (!string.IsNullOrEmpty(merged))
                {
                    html.Append(" style=\"").Append(WebUtility.HtmlEncode(merged)).Append('"');
                }
            }

            if (part.EmitNodeStyle)
            {
                AppendStyle(context.CurrentNode, html);
            }
        }

        if (part.AttributesAfterIdentity)
        {
            AppendPartAttributes(attributes, html);
        }

        if (generator is { } generatorSpec)
        {
            // 生成属性排在所有常规属性与内联样式之后、开标签收尾之前，
            // 与直出分支的「节点属性 → style → 生成属性」顺序一致。
            generatorSpec.Writer(new PartGeneratorContext
            {
                Node = context.CurrentNode,
                Source = ResolveGeneratorSource(context.CurrentNode),
                Html = html,
                Diagnostics = context.Diagnostics,
                Parameters = generatorSpec.Parameters,
                Phase = PartGeneratorPhase.Attributes,
                AttributeSink = (name, value) =>
                    html.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"'),
            });
        }

        if (part.SelfClosing)
        {
            html.Append(" />");
            return;
        }

        html.Append('>');

        if (generator is { } contentGenerator)
        {
            contentGenerator.Writer(new PartGeneratorContext
            {
                Node = context.CurrentNode,
                Source = ResolveGeneratorSource(context.CurrentNode),
                Html = html,
                Diagnostics = context.Diagnostics,
                Parameters = contentGenerator.Parameters,
                Phase = PartGeneratorPhase.Content,
            });
        }
        else if (part.Content is not null)
        {
            part.Content(context);
        }
        else if (template.Slots.TryGetValue(part.Part, out var writer))
        {
            writer(context);
        }

        if (generator is null)
        {
            foreach (var child in part.Children)
            {
                RenderPart(context, child, template);
            }
        }

        html.Append("</").Append(tag).Append('>');
    }

    /// <summary>
    /// 虚拟容器按“成员收集声明”渲染子节点：只收集集合定义的类型，
    /// 不再在渲染期写 <c>child.Type != X</c>；集合为空时写固定占位。
    /// </summary>
    private void RenderChildCollection(TemplateRenderContext context, PartTemplate part)
    {
        var rendered = false;
        foreach (var child in context.CurrentNode.Children)
        {
            if (!IsCollectionMember(part.ChildCollection!, child))
            {
                continue;
            }

            rendered = true;
            RenderNode(child, context.Html, context.Diagnostics);
        }

        if (!rendered && part.EmptyCollectionHtml is not null)
        {
            context.Html.Append(part.EmptyCollectionHtml);
        }
    }

    /// <summary>成员收集声明的固定映射：集合名 → 唯一成员类型（结构约束，不是元素外形）。</summary>
    private static bool IsCollectionMember(string collection, MarkdownNode child) =>
        collection switch
        {
            "timeline-items" => child.Type == NodeType.TimelineItem,
            _ => false,
        };

    /// <summary>单块重复的总展开上限：外层 ≤32、内层 ≤64、合计 ≤1024。</summary>
    private const int MaxRepeatExpansion = 1024;

    /// <summary>
    /// 生成器源文本：围栏类节点取自身正文；容器节点（如自定义元素）取块体内容。
    /// 这是结构事实（块体在子节点上），不按元素类型名分派。
    /// </summary>
    private static string ResolveGeneratorSource(MarkdownNode node)
    {
        if (node.Category != NodeCategory.Container)
        {
            return node.Text ?? string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var child in node.Children)
        {
            var text = child.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(text.TrimEnd('\r', '\n'));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 按名字取生成算法并解析封闭参数。名字/参数非法只影响本部件，不改变类型分派：
    /// 名字非法 → 丢弃生成器；参数非法 → 只丢参数。任何情况下都不回退到按类型写的分支。
    /// </summary>
    private (PartGenerator Writer, IReadOnlyDictionary<string, string> Parameters)? ResolvePartGenerator(
        TemplateRenderContext context,
        PartTemplate part)
    {
        if (string.IsNullOrEmpty(part.Generator))
        {
            return null;
        }

        if (!UserBlockStructureRules.TryNormalizeGenerator(part.Generator, out var name))
        {
            context.Diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Error,
                Code = "ELEMENT_GENERATOR_UNKNOWN",
                Message = "生成器名不在固定注册表：" + part.Generator,
            });
            return null;
        }

        if (!_generators.TryGetValue(name, out var writer))
        {
            context.Diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Error,
                Code = "ELEMENT_GENERATOR_UNIMPLEMENTED",
                Message = "生成器已在注册表声明但渲染器未实现：" + name,
            });
            return null;
        }

        string? normalizedParams = null;
        if (!UserBlockStructureRules.TryNormalizeGeneratorParams(
                name,
                part.GeneratorParams,
                out normalizedParams,
                out var errorCode))
        {
            context.Diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Error,
                Code = errorCode ?? "ELEMENT_GENERATOR_PARAMS_INVALID",
                Message = "生成器参数被封闭参数表拒绝：" + name,
            });
        }

        return (writer, ParseGeneratorParameters(normalizedParams));
    }

    private static IReadOnlyDictionary<string, string> ParseGeneratorParameters(string? normalized)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(normalized))
        {
            return result;
        }

        foreach (var token in normalized.Split(';'))
        {
            var separator = token.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            result[token[..separator]] = token[(separator + 1)..];
        }

        return result;
    }

    /// <summary>条件标签求值：只读节点已解析样式，不引入按类型的判断。</summary>
    private bool ShouldEmitPartTag(PartTagCondition condition, MarkdownNode node) => condition switch
    {
        PartTagCondition.HasInlineStyle => !_fragmentOptions.SuppressInlineStyle
            && !string.IsNullOrWhiteSpace(_cssStyleBuilder.Build(_styleResolver.Resolve(node))),
        _ => true,
    };

    /// <summary>条件不成立时只写部件内容与子部件，不发射标签与属性。</summary>
    private void RenderPartContentOnly(
        TemplateRenderContext context,
        PartTemplate part,
        ElementTemplate template)
    {
        if (part.Content is not null)
        {
            part.Content(context);
        }
        else if (template.Slots.TryGetValue(part.Part, out var writer))
        {
            writer(context);
        }

        foreach (var child in part.Children)
        {
            RenderPart(context, child, template);
        }
    }

    private static readonly List<(string Name, string Value)> EmptyAttributes = [];

    /// <summary>
    /// 解析部件动态属性。返回 null 表示整个部件不得发射（图片 URL 不安全）；
    /// 非法属性名/非法 URL 逐条诊断后跳过，不改变其余输出。
    /// </summary>
    private List<(string Name, string Value)>? BuildPartAttributes(
        TemplateRenderContext context,
        PartTemplate part)
    {
        if (part.Attributes.Count == 0)
        {
            return EmptyAttributes;
        }

        List<(string Name, string Value)>? values = null;

        foreach (var attribute in part.Attributes)
        {
            if (!PartAttributeRules.IsAllowedName(attribute.Name))
            {
                context.Diagnostics.Add(new DiagnosticMessage
                {
                    Severity = DiagnosticSeverity.Error,
                    Code = PartAttributeRules.ForbiddenNameCode,
                    Message = "动态属性名不在白名单：" + attribute.Name,
                });
                continue;
            }

            if (!PartAttributeRules.IsAllowedSource(attribute))
            {
                context.Diagnostics.Add(new DiagnosticMessage
                {
                    Severity = DiagnosticSeverity.Error,
                    Code = PartAttributeRules.ForbiddenSourceCode,
                    Message = "动态属性取值来源被固定枚举拒绝：" + attribute.Name,
                });

                if (string.Equals(attribute.Policy, "image", StringComparison.Ordinal))
                {
                    return null;
                }

                continue;
            }

            if (!TryResolvePartAttributeValue(context, attribute, out var value))
            {
                // 受控 URL 绑定取不到值（字段缺失/为空）：
                // href 只跳过该属性，部件照常发射；src 则整个部件不发射，绝不输出无 src 的 <img>。
                if (attribute.Source == PartAttributeValueSource.DeclaredFieldUrl)
                {
                    if (string.Equals(attribute.Name, UserBlockStructureRules.SrcAttribute, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    continue;
                }

                // 图片没有可用的 URL 与直出分支的“整段不发射”同义，不能退化成无 src 的 img。
                if (string.Equals(attribute.Policy, "image", StringComparison.Ordinal))
                {
                    return null;
                }

                continue;
            }

            if (!PartAttributeRules.IsAllowedValue(attribute, value))
            {
                // 固定名对应的值域被钉死（如 data-side 只接受 left/right），越界即丢该属性。
                context.Diagnostics.Add(new DiagnosticMessage
                {
                    Severity = DiagnosticSeverity.Error,
                    Code = PartAttributeRules.ForbiddenSourceCode,
                    Message = "动态属性取值超出固定值域：" + attribute.Name,
                });
                continue;
            }

            if (PartAttributeRules.IsUrlAttribute(attribute.Name))
            {
                // 受控 URL 绑定走解码后的协议白名单（HTML 实体/百分号/控制字符/大小写变形都先归一）；
                // 内置模板仍走既有 URL 策略，两类通道互不替代。
                var isBoundUrl = attribute.Source == PartAttributeValueSource.DeclaredFieldUrl;
                var safe = isBoundUrl
                    ? UserBlockStructureRules.IsSafeBoundUrl(value)
                    : string.Equals(attribute.Policy, "image", StringComparison.Ordinal)
                        ? IsSafeImageUrl(value)
                        : IsSafeUrl(value);

                if (!safe)
                {
                    context.Diagnostics.Add(new DiagnosticMessage
                    {
                        Severity = DiagnosticSeverity.Error,
                        Code = isBoundUrl
                            ? "USER_ELEMENTS_STRUCTURE_BIND_URL_UNSAFE"
                            : PartAttributeRules.UnsafeUrlCode,
                        Message = "动态属性 URL 被安全策略拒绝：" + attribute.Name,
                    });

                    if (isBoundUrl
                        && string.Equals(attribute.Name, UserBlockStructureRules.SrcAttribute, StringComparison.Ordinal))
                    {
                        // 受控 src 不安全：整个部件不发射，与“没有可用图片 URL”同口径。
                        return null;
                    }

                    if (!isBoundUrl && string.Equals(attribute.Policy, "image", StringComparison.Ordinal))
                    {
                        return null;
                    }

                    continue;
                }
            }

            (values ??= []).Add((attribute.Name, value!));
        }

        return values ?? EmptyAttributes;
    }

    /// <summary>固定枚举求值：不含表达式、拼接模板或格式化字符串。</summary>
    private static bool TryResolvePartAttributeValue(
        TemplateRenderContext context,
        PartAttributeTemplate attribute,
        out string? value)
    {
        value = attribute.Source switch
        {
            PartAttributeValueSource.Constant => attribute.Value,
            PartAttributeValueSource.NodeAttribute => attribute.Value is null
                ? null
                : GetAttribute(context.CurrentNode, attribute.Value),
            PartAttributeValueSource.DeclaredFieldUrl => attribute.Value is null
                ? null
                : ResolveDeclaredField(context.CurrentNode, attribute.Value),
            PartAttributeValueSource.NodeText => context.CurrentNode.Text ?? string.Empty,
            PartAttributeValueSource.RefBackrefItem => context.RepeatItem,
            PartAttributeValueSource.RefBackrefHref => context.RepeatItem is null
                ? null
                : "#" + context.RepeatItem,
            PartAttributeValueSource.RefBackrefTitle => context.RepeatOrdinal is int titleOrdinal
                ? "返回第" + titleOrdinal + "处引用"
                : null,
            PartAttributeValueSource.RefBackrefAriaLabel => context.RepeatOrdinal is int labelOrdinal
                ? "返回第" + labelOrdinal + "处正文引用"
                : null,
            PartAttributeValueSource.RefTarget => "fn-"
                + (GetAttribute(context.CurrentNode, "slug") ?? context.CurrentNode.Text),
            _ => null,
        };

        return value is not null;
    }

    private static void AppendPartAttributes(
        List<(string Name, string Value)> attributes,
        StringBuilder html)
    {
        foreach (var (name, value) in attributes)
        {
            html.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');
        }
    }

    private readonly record struct PartBinding(
        ElementAttributeBindingTarget Target,
        string Name,
        string Value);

    /// <summary>
    /// 取该部件命中的类型级绑定。绑定只决定“往已有部件上写什么”，
    /// 不会增删部件；任何一项校验失败都整条拒绝。
    /// </summary>
    private List<PartBinding>? ResolvePartBindings(
        TemplateRenderContext context,
        NodeType nodeType,
        string partName)
    {
        // 旁路「绑属性」的实验态：模板照常，但类型级属性绑定不参与。
        if (_fragmentOptions.SuppressBindings)
        {
            return null;
        }

        if (_activeTypeDefinitions is null
            || !_activeTypeDefinitions.TryGet(nodeType, ElementKind.Resolve(context.Node), out var definition)
            || definition.Attributes.Count == 0)
        {
            return null;
        }

        List<PartBinding>? result = null;

        // 校验与部件无关：在根部件上把所有非法绑定报一次，避免逐部件/逐实例重复提示。
        if (string.Equals(partName, PartTokens.Root, StringComparison.Ordinal))
        {
            foreach (var candidate in definition.Attributes)
            {
                var candidateBinding = candidate.Binding;
                if (candidateBinding is null || candidateBinding.IsEmpty)
                {
                    continue;
                }

                var validation = ElementRenderBindingRules.Validate(
                    nodeType,
                    candidate,
                    candidateBinding);
                if (!validation.IsValid)
                {
                    context.Diagnostics.Add(new DiagnosticMessage
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Code = "BINDING_INVALID",
                        Message = $"属性「{candidate.Key}」的呈现绑定被拒绝：{validation.Message}",
                        Range = context.Node.Range,
                    });
                }
            }
        }

        foreach (var declaration in definition.Attributes)
        {
            var binding = declaration.Binding;
            if (binding is null || binding.IsEmpty)
            {
                continue;
            }

            var targetPart = string.IsNullOrWhiteSpace(binding.Part)
                ? PartTokens.Root
                : binding.Part.Trim();
            if (!string.Equals(targetPart, partName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!ElementRenderBindingRules.Validate(nodeType, declaration, binding).IsValid)
            {
                continue;
            }

            var raw = context.Node.Attributes.TryGetValue(declaration.Key, out var value)
                ? value
                : declaration.DefaultValue;
            var resolved = ElementRenderBindingRules.ResolveValue(
                binding.Target,
                binding.ValueMap,
                raw);
            if (resolved is null)
            {
                continue;
            }

            (result ??= new List<PartBinding>(4))
                .Add(new PartBinding(binding.Target, binding.Name, resolved));
        }

        return result;
    }

    private static string? BuildBindingClassTokens(List<PartBinding>? bindings)
    {
        if (bindings is null) return null;

        StringBuilder? builder = null;
        foreach (var binding in bindings)
        {
            if (binding.Target != ElementAttributeBindingTarget.ClassToken) continue;
            builder ??= new StringBuilder();
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(binding.Value);
        }

        return builder?.ToString();
    }

    private static string? BuildBindingCss(List<PartBinding>? bindings)
    {
        if (bindings is null) return null;

        StringBuilder? builder = null;
        foreach (var binding in bindings)
        {
            if (binding.Target != ElementAttributeBindingTarget.CssVar) continue;
            builder ??= new StringBuilder();
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(binding.Name).Append(": ").Append(binding.Value).Append(';');
        }

        return builder?.ToString();
    }

    private static void AppendBindingDataAttributes(List<PartBinding>? bindings, StringBuilder html)
    {
        if (bindings is null) return;

        foreach (var binding in bindings)
        {
            if (binding.Target != ElementAttributeBindingTarget.DataAttribute) continue;
            html.Append(' ')
                .Append(binding.Name)
                .Append("=\"")
                .Append(WebUtility.HtmlEncode(binding.Value))
                .Append('"');
        }
    }

    private void RegisterTemplates()
    {
        // 全部内置结构都来自嵌入的数据面 builtin-elements.xml（含提示块的属性变体、
        // 自定义块档位与时间线布局）；这里只把定义编译成模板并登记，
        // 不再有任何按类型构造结构的代码。
        RegisterBuiltInDefinitions();
    }

    /// <summary>
    /// 内置结构描述全部来自嵌入的数据面 <c>builtin-elements.xml</c>：
    /// 这里只把定义编译成模板并登记，结构、条件、重复、内容槽与配方都由数据声明。
    /// 与用户定义的图路径共用同一个 <see cref="CreateDeclaredPartTemplate"/>。
    /// </summary>
    private void RegisterBuiltInDefinitions() => RegisterBuiltInDefinitions(BuiltInElementDefinitions.Load());

    /// <summary>
    /// 内置结构描述全部来自嵌入的数据面 <c>builtin-elements.xml</c>：
    /// 这里只把定义编译成模板并登记，结构、条件、重复、内容槽与配方都由数据声明。
    /// 与用户定义的图路径共用同一个 <see cref="CreateDeclaredPartTemplate"/>。
    /// </summary>
    /// <remarks>
    /// <b>没有结构声明（<see cref="UserBlockStructure.Root"/> 为 null）的定义不注册模板</b>：
    /// 这类定义只服务数据面本身（例如行内 HTML 的解析期白名单），外形由实例决定，
    /// 没有可编译的静态模板。判据只看数据，不认类型名。
    /// </remarks>
    internal void RegisterBuiltInDefinitions(IEnumerable<UserBlockDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            if (!Enum.TryParse<NodeType>(definition.AppliesTo, ignoreCase: true, out var nodeType))
            {
                throw new InvalidOperationException(
                    $"内置元素描述指向未知类型：{definition.Id}（applies-to={definition.AppliesTo}）。");
            }

            if (definition.Structure.Root is not { } root)
            {
                continue;
            }

            var variant = ResolveDeclaredVariant(definition.Variant);
            var template = new ElementTemplate
            {
                NodeType = nodeType,
                Variant = variant,
                Root = BuildStructurePart(root, isRoot: true, customKind: false, variant),
                RenderEnabled = definition.RenderEnabled ?? true,
                StructureKey = string.Equals(definition.StructureKey, "poem", StringComparison.Ordinal)
                    ? ElementStructureKey.Poem
                    : ElementStructureKey.None,
            };

            InheritNamedSlotWriters(template.Root, template);

            if (definition.Selector is not { } selector)
            {
                _templates[(nodeType, variant)] = template;
                continue;
            }

            if (!TryParseSelector(selector, out var key, out var match))
            {
                throw new InvalidOperationException(
                    $"内置元素描述的选择器无法解析：{definition.Id}（selector={selector}）。");
            }

            // 命中侧没有专属结构时回落到 (类型, 变体) 注册表：admonition 的存在侧就是通用形态。
            RegisterTemplateSelector(nodeType, key, TemplateSelectorMatch.Present);
            RegisterSelectedTemplate(nodeType, match, variant, template);
        }
    }

    /// <summary>测试用探针：查询某个 (类型, 变体) 是否登记了块模板。</summary>
    internal ElementTemplate? FindRegisteredTemplate(NodeType nodeType, ElementTemplateVariant variant) =>
        _templates.TryGetValue((nodeType, variant), out var template) ? template : null;

    /// <summary>定义里的档位名 → 模板变体；缺省为 Default，非法值在加载阶段已被结构校验拒绝。</summary>
    private static ElementTemplateVariant ResolveDeclaredVariant(string? variant) =>
        variant is not null
        && Enum.TryParse<ElementTemplateVariant>(variant, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : ElementTemplateVariant.Default;

    /// <summary>选择器名 → 固定选择键 + 命中侧；名字由结构校验保证是封闭集合。</summary>
    private static bool TryParseSelector(
        string selector,
        out TemplateSelectorKey key,
        out TemplateSelectorMatch match)
    {
        key = TemplateSelectorKey.Admonition;
        switch (selector)
        {
            case "admonition:present":
                match = TemplateSelectorMatch.Present;
                return true;
            case "admonition:absent":
                match = TemplateSelectorMatch.Absent;
                return true;
            default:
                match = TemplateSelectorMatch.Absent;
                return false;
        }
    }

    /// <summary>
    /// 部件自带的命名内容写入器同时登记为该槽位的共享写入器：
    /// 用户定义重写内置类型结构时按槽位名继承，换结构不丢“图标/标题/正文”的语义。
    /// </summary>
    private static void InheritNamedSlotWriters(
        PartTemplate part,
        ElementTemplate template,
        bool isRoot = true)
    {
        if (!isRoot && part.Content is { } writer)
        {
            template.Slots[part.Part] = writer;
        }

        foreach (var child in part.Children)
        {
            InheritNamedSlotWriters(child, template, isRoot: false);
        }
    }

    /// <summary>行内部件内容：有子节点写子节点，无子节点写节点文本（与直出 RenderInline 一致）。</summary>
    private void WriteInlineContent(TemplateRenderContext context)
    {
        if (context.Node.Children.Count == 0)
        {
            context.Html.Append(WebUtility.HtmlEncode(context.Node.Text));
        }
        else
        {
            RenderChildren(context.Node, context.Html, context.Diagnostics);
        }
    }

    /// <summary>任务列表正文：固定 checkbox + 空格 + 紧容器子节点。</summary>
    private void WriteTaskListItemContent(TemplateRenderContext context)
    {
        var isChecked = context.Node.Attributes.TryGetValue("checked", out var checkedValue)
                        && string.Equals(checkedValue, "true", StringComparison.OrdinalIgnoreCase);

        context.Html.Append("<input type=\"checkbox\" disabled");
        if (isChecked) context.Html.Append(" checked");
        context.Html.Append("> ");

        RenderTightContainerChildren(context.Node, context.Html, context.Diagnostics);
    }

    /// <summary>代码块正文：子节点文本拼接（无子节点用节点文本），去尾部换行后转义。</summary>
    private static void WriteCodeBlockText(TemplateRenderContext context)
    {
        var code = new StringBuilder();

        if (context.Node.Children.Count == 0)
        {
            code.Append(context.Node.Text);
        }
        else
        {
            for (int i = 0; i < context.Node.Children.Count; i++)
            {
                code.Append(context.Node.Children[i].Text);
            }
        }

        AppendEncoded(context.Html, code.ToString().TrimEnd('\r', '\n'));
    }

    /// <summary>脚注序号/引用显示文本：优先 label，其次 slug，最后节点文本。</summary>
    private static void WriteFootnoteLabel(TemplateRenderContext context)
    {
        AppendEncoded(
            context.Html,
            GetAttribute(context.Node, "label")
            ?? GetAttribute(context.Node, "slug")
            ?? context.Node.Text);
    }

    /// <summary>链接正文：有文本用文本，否则渲染子节点（与直出分支同序）。</summary>
    private void WriteLinkContent(TemplateRenderContext context)
    {
        if (!string.IsNullOrEmpty(context.Node.Text))
        {
            context.Html.Append(WebUtility.HtmlEncode(context.Node.Text));
        }
        else
        {
            RenderChildren(context.Node, context.Html, context.Diagnostics);
        }
    }

    /// <summary>代码块语言类：与渲染分支同一取值规则（首词、去空白）。</summary>
    private static string? ResolveLanguageClass(MarkdownNode node)
    {
        if (!node.Attributes.TryGetValue("language", out var language)
            || string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var tokens = language.Trim()
            .Split(LanguageSeparators, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 ? null : "language-" + tokens[0];
    }

    /// <summary>二维码状态类：未闭合与空内容各自追加，取值与渲染分支一致。</summary>
    private static string? ResolveQrCodeStateClass(MarkdownNode node)
    {
        var pending = node.Attributes.ContainsKey(MarkdownAttributes.Unclosed);
        var empty = string.IsNullOrWhiteSpace(node.Text);
        return (pending, empty) switch
        {
            (true, true) => "qrcode-block-pending qrcode-block-empty",
            (true, false) => "qrcode-block-pending",
            (false, true) => "qrcode-block-empty",
            _ => null,
        };
    }

    /// <summary>雷达图状态类：未闭合与数据不可解析各自追加。</summary>
    private static string? ResolveRadarStateClass(MarkdownNode node)
    {
        var pending = node.Attributes.ContainsKey(MarkdownAttributes.Unclosed);
        var empty = RadarMarker.ParseTable(node.Text) is null;
        return (pending, empty) switch
        {
            (true, true) => "md-radar-pending md-radar-empty",
            (true, false) => "md-radar-pending",
            (false, true) => "md-radar-empty",
            _ => null,
        };
    }

    /// <summary>固定选择键求值：只判断属性存在/缺失，不接受取值、表达式或组合键。</summary>
    private static TemplateSelectorMatch ResolveSelectorMatch(TemplateSelectorKey key, MarkdownNode node) =>
        key switch
        {
            TemplateSelectorKey.Admonition => node.Attributes.ContainsKey("admonition")
                ? TemplateSelectorMatch.Present
                : TemplateSelectorMatch.Absent,
            _ => TemplateSelectorMatch.Absent,
        };

    /// <summary>
    /// 选择结构按变体登记：某一变体没有专属结构时沿用 Default 变体的同一匹配结构，
    /// 与历史“选择器结构不分变体”的行为一致。
    /// </summary>
    private bool TryGetSelectedTemplate(
        NodeType type,
        TemplateSelectorMatch match,
        ElementTemplateVariant variant,
        [NotNullWhen(true)] out ElementTemplate? template)
    {
        if (_selectedTemplates.TryGetValue((type, match, variant), out template))
        {
            return true;
        }

        return variant != ElementTemplateVariant.Default
               && _selectedTemplates.TryGetValue((type, match, ElementTemplateVariant.Default), out template);
    }

    /// <summary>该节点实际使用的内置模板：选择器命中的结构优先，未命中回落 (类型, 变体) 注册表。</summary>
    private ElementTemplate? ResolveBaseTemplate(MarkdownNode node, ElementTemplateVariant variant)
    {
        if (_templateSelectors.TryGetValue(node.Type, out var selector))
        {
            var match = ResolveSelectorMatch(selector.Key, node);
            if (TryGetSelectedTemplate(node.Type, match, variant, out var selected))
            {
                return selected;
            }

            if (selector.FallbackMatch != match)
            {
                return null;
            }
        }

        return _templates.TryGetValue((node.Type, variant), out var template) ? template : null;
    }

    /// <summary>声明类型使用哪个固定选择键；未声明的类型不会命中任何选择结构。</summary>
    private void RegisterTemplateSelector(
        NodeType type,
        TemplateSelectorKey key,
        TemplateSelectorMatch fallbackMatch) =>
        _templateSelectors[type] = new TemplateSelectorBinding(key, fallbackMatch);

    /// <summary>登记选择器命中的结构；未声明选择器的类型直接拒绝，避免变成全局开关。</summary>
    private void RegisterSelectedTemplate(
        NodeType type,
        TemplateSelectorMatch match,
        ElementTemplateVariant variant,
        ElementTemplate template)
    {
        if (!_templateSelectors.ContainsKey(type))
        {
            throw new InvalidOperationException("未声明结构选择器的类型不得注册选择结构：" + type);
        }

        _selectedTemplates[(type, match, variant)] = template;
    }

    private void WriteAdmonitionIcon(TemplateRenderContext context)
    {
        var safeKind = ResolveAdmonitionKind(
            context.Node.Attributes.GetValueOrDefault("admonition") ?? string.Empty);
        var icon = ResolveAdmonitionIcon(context.Node, safeKind);
        context.Html.Append("<span class=\"md-icon\" aria-hidden=\"true\">")
            .Append(icon)
            .Append("</span>");
    }

    private void WriteAdmonitionTitleText(TemplateRenderContext context)
    {
        var safeKind = ResolveAdmonitionKind(
            context.Node.Attributes.GetValueOrDefault("admonition") ?? string.Empty);
        context.Html.Append(WebUtility.HtmlEncode(ResolveAdmonitionTitle(context.Node, safeKind)));
    }

    private static string ResolveAdmonitionKind(string kind) =>
        AdmonitionCatalog.AdmonitionKinds.Contains(kind)
            ? kind
            : AdmonitionCatalog.DefaultAdmonitionKind;

    private static string ResolveAdmonitionTitle(MarkdownNode node, string safeKind) =>
        node.Attributes.TryGetValue("admonition-title", out var titleValue)
        && !string.IsNullOrWhiteSpace(titleValue)
            ? titleValue.Trim()
            : AdmonitionCatalog.GetDefaultTitle(safeKind);

    private string ResolveAdmonitionIcon(MarkdownNode node, string safeKind) =>
        TryBuildIconGlyph(node.Attributes.GetValueOrDefault(MarkdownAttributes.Icon))
        ?? _activeIcons.GetAdmonitionIcon(safeKind);

    /// <summary>来源部件正文：写当前来源节点的文本（已转义）。</summary>
    private static void WriteCurrentNodeText(TemplateRenderContext context) =>
        AppendEncoded(context.Html, context.CurrentNode.Text ?? string.Empty);

    /// <summary>时间线条目时间：空时间写固定占位，非空写原文（已转义）。</summary>
    private static void WriteTimelineTime(TemplateRenderContext context)
    {
        var time = GetAttributeStatic(context.Node, "timeline-time");
        AppendEncoded(
            context.Html,
            string.IsNullOrWhiteSpace(time) ? "未设置时间" : time);
    }

    /// <summary>时间线主轴类：档位决定主轴位置（edge/axis），再叠加变体、虚线与未闭合标记。</summary>
    private static string BuildTimelineVariantClass(MarkdownNode node, ElementTemplateVariant variant)
    {
        var builder = new StringBuilder(
            variant is ElementTemplateVariant.Left or ElementTemplateVariant.Right
                ? "md-timeline-edge"
                : "md-timeline-axis");

        switch (variant)
        {
            case ElementTemplateVariant.Left:
                builder.Append(" md-timeline-variant-left");
                break;
            case ElementTemplateVariant.Right:
                builder.Append(" md-timeline-variant-right");
                break;
            case ElementTemplateVariant.Center:
                builder.Append(" md-timeline-variant-center");
                break;
        }

        if (GetAttributeStatic(node, "timeline-line") == "dashed")
        {
            builder.Append(" md-timeline-line-dashed");
        }

        if (node.Attributes.ContainsKey(MarkdownAttributes.Unclosed))
        {
            builder.Append(" md-timeline-pending");
        }

        return builder.ToString();
    }

    private void WriteCustomBlockIcon(TemplateRenderContext context)
    {
        var kind = ResolveCustomBlockKind(context.Node);
        var icon = TryBuildIconGlyph(context.Node.Attributes.GetValueOrDefault(MarkdownAttributes.Icon))
            ?? _activeIcons.GetCustomBlockIcon(kind);
        context.Html.Append("<span class=\"md-icon\" aria-hidden=\"true\">")
            .Append(icon)
            .Append("</span>");
    }

    // 自定义元素的 kind 直接取节点属性：不再夹取到内置类别集合，
    // 否则用户定义的 kind（如 note / callout）会被归并成同一类别而失去独立性。
    // 只做 class/字典键所需的安全规范化；非法字符回退默认 kind，避免拼进 HTML。
    private static string ResolveCustomBlockKind(MarkdownNode node) =>
        ElementKind.NormalizeToken(ElementKind.Resolve(node))
            ?? AdmonitionCatalog.DefaultCustomBlockKind;

    private static string ResolveCustomBlockKindClass(MarkdownNode node) =>
        "custom-" + ResolveCustomBlockKind(node);

    private void RenderInline(MarkdownNode node, string tag, StringBuilder html, List<DiagnosticMessage> diagnostics)
    {
        html.Append('<').Append(tag);
        AppendNodeAttributes(node, html);
        AppendStyle(node, html);
        html.Append('>');
        if (node.Children.Count == 0)
        {
            html.Append(WebUtility.HtmlEncode(node.Text));
        }
        else
        {
            RenderChildren(node, html, diagnostics);
        }
        html.Append("</").Append(tag).Append('>');
    }

    /// <summary>公式生成器：原文进 <c>data-tex</c>，正文是原文（转义由生成器负责）。</summary>
    private static void WriteMathGenerator(PartGeneratorContext context)
    {
        var formula = context.Source ?? string.Empty;

        if (context.Phase == PartGeneratorPhase.Attributes)
        {
            context.AppendAttribute("data-tex", formula);
            return;
        }

        context.Html.Append(WebUtility.HtmlEncode(formula));
    }

    /// <summary>
    /// <c>code-highlight</c> 生成器：源文本取块体，语言是封闭枚举（<c>lang</c> 必填），
    /// 输出只含受控 token 标签且文本统一转义。语言缺失/非法时直接给固定诊断码，
    /// 绝不退化成“无高亮的纯文本”并当作成功。
    /// </summary>
    private static void WriteCodeHighlightGenerator(PartGeneratorContext context)
    {
        var language = context.Parameter("lang");
        if (string.IsNullOrEmpty(language) || !UserBlockStructureRules.IsCodeHighlightLanguage(language))
        {
            if (context.Phase == PartGeneratorPhase.Attributes)
            {
                return;
            }

            context.Diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Error,
                Code = "ELEMENT_GENERATOR_LANG_INVALID",
                Message = "code-highlight 必须声明封闭枚举内的 lang；本次不输出内容。",
            });
            return;
        }

        var lineNumbers = string.Equals(
            context.Parameter("linenums"),
            "on",
            StringComparison.OrdinalIgnoreCase);

        if (context.Phase == PartGeneratorPhase.Attributes)
        {
            context.AppendAttribute("data-lang", language);
            if (lineNumbers)
            {
                context.AppendAttribute("data-linenums", "on");
            }

            return;
        }

        var source = context.Source;
        if (string.IsNullOrEmpty(source))
        {
            return;
        }

        CodeHighlighter.Write(source, language, lineNumbers, context.Html);
    }

    /// <summary>
    /// KaTeX 注入的 .katex 元素自带 font-family/text-align，容器直接属性无法继承穿透；
    /// 用户设置改写为 --math-* 变量由预览 CSS 消费，未设置时回退 KaTeX 默认。
    /// </summary>
    private string? BuildMathStyleVars(MarkdownNode node)
    {
        var resolved = _styleResolver.Resolve(node);
        StringBuilder? vars = null;

        var align = resolved.TextAlign?.Trim().ToLowerInvariant();
        if (align is "left" or "right" or "center")
        {
            vars = new StringBuilder();
            vars.Append("--math-align: ").Append(align).Append(';');
        }

        // KaTeX 暗色主题规则（html[data-theme="dark"] .katex）特异性高于
        // 普通 .katex 取色规则，会顶掉继承下来的 --node-color；显式色
        // 改写为 --math-color 由 .math-block 专属规则消费。
        var color = resolved.ForegroundColor?.Trim();
        // 与 PreviewCssStyleBuilder 的基线剥离保持一致：生效值等于文档默认
        // 文字色（#333333 等）时只是默认基线而非用户显式设置，不输出变量，
        // 否则暗色主题下公式被默认深灰锁死（灰底灰字）。
        var defaultColor = _defaultStyle?.ForegroundColor?.Trim();
        if (!string.IsNullOrWhiteSpace(color) && !SameHexColor(color, defaultColor))
        {
            vars ??= new StringBuilder();
            vars.Append("--math-color: ").Append(color).Append(';');
        }

        var fontFamily = resolved.FontFamily?.Trim();
        // 与 PreviewCssStyleBuilder 的基线剥离保持一致：生效值等于文档默认
        // 字体（Arial 等）时视为用户未显式设置，不输出变量，交由 KaTeX 默认。
        var defaultFontFamily = _defaultStyle?.FontFamily?.Trim();
        if (!string.IsNullOrWhiteSpace(fontFamily)
            && !string.Equals(fontFamily, defaultFontFamily, StringComparison.OrdinalIgnoreCase))
        {
            vars ??= new StringBuilder();
            vars.Append("--math-font-family: ")
                .Append(fontFamily)
                .Append(';');
        }

        return vars?.ToString();
    }

    // 颜色字面量等价比较：#abc 展开为 #aabbcc，与 PreviewCssStyleBuilder
    // 的归一化规则一致。任一为空视为不等。
    private static bool SameHexColor(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        return string.Equals(NormalizeHexColor(a), NormalizeHexColor(b), StringComparison.Ordinal);
    }

    private static string NormalizeHexColor(string color)
    {
        var c = color.Trim().ToLowerInvariant();
        if (c.Length == 4 && c[0] == '#')
        {
            c = "#" + c[1] + c[1] + c[2] + c[2] + c[3] + c[3];
        }

        return c;
    }

    /// <summary>Mermaid 生成器：源文本进 <c>data-diagram</c>，正文是源文本。</summary>
    private static void WriteMermaidGenerator(PartGeneratorContext context)
    {
        var source = (context.Source ?? string.Empty).TrimEnd('\r', '\n');

        if (context.Phase == PartGeneratorPhase.Attributes)
        {
            context.AppendAttribute("data-diagram", source);
            return;
        }

        context.Html.Append(WebUtility.HtmlEncode(source));
    }

    /// <summary>图片矩阵生成器：按行/格产出内容；外层容器与状态类由描述负责。</summary>
    private static void WriteImageMatrixGenerator(PartGeneratorContext context)
    {
        if (context.Phase == PartGeneratorPhase.Attributes)
        {
            return;
        }

        var rows = CollectMatrixRows(context.Source ?? string.Empty);
        foreach (var row in rows)
        {
            context.Html.Append("<div class=\"image-matrix-row\">");
            foreach (var cell in row)
            {
                AppendMatrixCell(context.Html, cell);
            }

            context.Html.Append("</div>");
        }
    }

    /// <summary>二维码生成器：完整内容进 <c>data-tip</c>，正文是 SVG/占位/错误块。</summary>
    private static void WriteQrCodeGenerator(PartGeneratorContext context)
    {
        // 块体文本即二维码承载内容（链接 / 任意文本）。
        var content = (context.Source ?? string.Empty).Trim();

        if (context.Phase == PartGeneratorPhase.Attributes)
        {
            // 完整内容进 data-tip 由预览 CSS 画主题化气泡（原生 title 样式系统级不可控）。
            if (content.Length > 0)
            {
                context.AppendAttribute("data-tip", content);
            }

            return;
        }

        var html = context.Html;
        if (content.Length == 0)
        {
            html.Append("<div class=\"qrcode-placeholder\">")
                .Append("<span class=\"qrcode-placeholder-icon\" aria-hidden=\"true\">&#9638;</span>")
                .Append("<span>在围栏内输入二维码内容（链接或文本）</span>")
                .Append("</div>");
            return;
        }

        try
        {
            // 定义声明的封闭参数优先；未声明参数的内置描述仍读节点属性。
            var eccValue = context.Parameter("ecc")
                           ?? (context.Node.Attributes.TryGetValue("qr-ecc", out var eccAttr) ? eccAttr : null);
            var borderValue = context.Parameter("border")
                              ?? (context.Node.Attributes.TryGetValue("qr-border", out var borderAttr) ? borderAttr : null);

            var ecc = ResolveQrEcc(eccValue);
            var border = ResolveQrBorder(borderValue);

            // 必须关闭 ECC 提升（boostEcl）：默认会把等级向上提升，
            // 短内容选 L/M/Q 最终都被提升到 Q。关闭后严格按所选等级编码。
            var qr = Net.Codecrete.QrCodeGenerator.QrCode.EncodeTextAdvanced(
                content,
                ecc,
                boostEcl: false,
                minVersion: 1,
                maxVersion: 40,
                eci: Net.Codecrete.QrCodeGenerator.ECI.Automatic,
                encoding: null,
                kanjiStrategy: Net.Codecrete.QrCodeGenerator.KanjiStrategy.Automatic,
                encodingInfo: null);

            var svg = qr.ToSvgString(border, "currentColor", "transparent");

            // 去掉 ToSvgString 自带的 <?xml?> 声明与 <!DOCTYPE>，
            // 只保留 <svg>…</svg> 以便内联嵌入。
            var svgStart = svg.IndexOf("<svg", StringComparison.Ordinal);
            if (svgStart > 0)
                svg = svg.Substring(svgStart);

            html.Append("<div class=\"qrcode-svg\">")
                .Append(svg)
                .Append("</div>")
                // 显示文本由 CSS 两行截断，超长链接不会在卡片下堆成高文字块。
                .Append("<figcaption>")
                .Append(WebUtility.HtmlEncode(content))
                .Append("</figcaption>");
        }
        catch (Net.Codecrete.QrCodeGenerator.DataTooLongException)
        {
            html.Append("<div class=\"qrcode-error\" role=\"alert\">")
                .Append("内容过长，无法生成二维码，请缩短内容")
                .Append("</div>");
        }
    }

    private static Net.Codecrete.QrCodeGenerator.QrCode.Ecc ResolveQrEcc(string? value) =>
        value switch
        {
            "L" => Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Low,
            "Q" => Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Quartile,
            "H" => Net.Codecrete.QrCodeGenerator.QrCode.Ecc.High,
            _ => Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Medium
        };

    private static int ResolveQrBorder(string? value) =>
        int.TryParse(value, out var modules) && modules is >= 0 and <= 10
            ? modules
            : 2;

    /// <summary>
    /// 拆分图片矩阵一行中的格子。以空白分隔，但双引号内的空格不参与分隔，
    /// 因此 <c>T:"山居 秋暝"</c> 这样的含空格文本可作为单个文本格。
    /// 引号本身不作为内容保留。
    /// </summary>
    private static List<string> SplitMatrixCells(string line)
    {
        var cells = new List<string>();
        var builder = new StringBuilder();
        var inCell = false;
        var inQuote = false;

        foreach (var ch in line)
        {
            if (inQuote)
            {
                if (ch == '"')
                {
                    inQuote = false;
                }
                else
                {
                    builder.Append(ch);
                }

                continue;
            }

            if (ch == '"')
            {
                inQuote = true;
                inCell = true;
                continue;
            }

            if (ch == ' ' || ch == '\t')
            {
                if (inCell)
                {
                    cells.Add(builder.ToString());
                    builder.Clear();
                    inCell = false;
                }

                continue;
            }

            builder.Append(ch);
            inCell = true;
        }

        // 引号未闭合时也保留已读取的内容。
        if (inCell)
        {
            cells.Add(builder.ToString());
        }

        return cells;
    }

    /// <summary>
    /// 输出图片矩阵中的一个格子：
    /// 显式 <c>T:文字</c>（含空文本占位）或普通纯文本渲染为文本格，
    /// 安全 URL 渲染为图片格；合法但 scheme 不被允许的 URI（如 javascript:）直接忽略。
    /// </summary>
    private static void AppendMatrixCell(
        StringBuilder html,
        string token)
    {
        string? text;

        if (token.StartsWith("T:", StringComparison.OrdinalIgnoreCase))
        {
            // 显式文本标记，冒号后即文本内容（引号已在拆分阶段去除）。
            text = token.Substring(2);
        }
        else if (IsSafeUrl(token))
        {
            html.Append("<figure class=\"image-matrix-cell image-matrix-cell-image\"><img src=\"")
                .Append(WebUtility.HtmlEncode(token))
                .Append("\" loading=\"lazy\" alt=\"\" /></figure>");
            return;
        }
        else if (Uri.TryCreate(token, UriKind.Absolute, out _))
        {
            // 形如绝对 URI 但 scheme 不在允许名单（javascript:/data:/vbscript: 等），
            // 既不作为图片也不作为文本，直接丢弃，防止危险内容注入。
            return;
        }
        else
        {
            // 非 URI 的普通文字，兼容为文本格。
            text = token;
        }

        var isEmpty = string.IsNullOrWhiteSpace(text);

        html.Append(
            "<figure class=\"image-matrix-cell image-matrix-text\"><div class=\"image-matrix-text-inner");

        if (isEmpty)
        {
            html.Append(" image-matrix-text-empty");
        }

        html.Append("\">");

        // 零宽空格保证空文本格仍占据与图片一致的 4:3 盒高度，不塌陷。
        html.Append(
            isEmpty
                ? "&#8203;"
                : WebUtility.HtmlEncode(text));

        html.Append("</div></figure>");
    }

    // 雷达图系列配色（循环使用）。
    private static readonly string[] RadarSeriesColors =
    {
        "#ef4444", "#3b82f6", "#22c55e", "#f59e0b",
        "#8b5cf6", "#06b6d4", "#ec4899", "#84cc16",
    };

    /// <summary>
    /// 渲染雷达图：解析围栏内表格，生成内联 SVG（同心网格 + 轴线 + 系列多边形 + 可选数值标签），
    /// 标题与底部图例用 HTML 承载以便文本排版。数据非法时显示占位提示。
    /// </summary>
    /// <summary>雷达图生成器：解析围栏内表格并生成图表内容；外层 figure 由描述负责。</summary>
    private static void WriteRadarGenerator(PartGeneratorContext context)
    {
        if (context.Phase == PartGeneratorPhase.Attributes)
        {
            return;
        }

        var node = context.Node;
        var html = context.Html;

        var title = GetAttribute(node, "radar-title") ?? string.Empty;
        var showLabels = context.Parameter("labels") == "show"
                         || (context.Parameter("labels") is null
                             && GetAttribute(node, "radar-labels") == "show");
        var showLegend = context.Parameter("legend") != "hide"
                         && (context.Parameter("legend") == "show"
                             || GetAttribute(node, "radar-legend") != "hide");
        var fillOpacity = RadarMarker.DefaultFillOpacity;
        if (int.TryParse(context.Parameter("fill") ?? GetAttribute(node, "radar-fill"),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var fill)
            && fill is >= RadarMarker.MinFillOpacity and <= RadarMarker.MaxFillOpacity)
        {
            fillOpacity = fill;
        }

        var table = RadarMarker.ParseTable(context.Source ?? string.Empty);

        if (table is null || table.Axes.Count < 3)
        {
            html.Append("<div class=\"md-radar-placeholder\">")
                .Append("<span class=\"md-radar-placeholder-icon\" aria-hidden=\"true\">◎</span>")
                .Append("<span>用表格定义雷达图：首行系列名，首列轴名，单元格填数值</span>")
                .Append("</div>");
            return;
        }

        var seriesCount = table.SeriesNames.Count;
        var axisCount = table.Axes.Count;

        // 量程：定义参数或显式 max 优先，否则取数据最大值向上取整到"好看"的数。
        double maxScale;
        var scaleParam = context.Parameter("scale");
        var scaleAttr = GetAttribute(node, "radar-scale");
        if (double.TryParse(scaleParam,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var paramMax)
            && paramMax > 0)
        {
            maxScale = paramMax;
        }
        else if (!string.IsNullOrWhiteSpace(scaleAttr)
            && scaleAttr.StartsWith("max:", StringComparison.Ordinal)
            && scaleAttr.Length > 4
            && double.TryParse(scaleAttr.Substring(4), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var explicitMax)
            && explicitMax > 0)
        {
            maxScale = explicitMax;
        }
        else
        {
            var dataMax = 0.0;
            foreach (var axis in table.Axes)
                foreach (var v in axis.Values)
                    if (v > dataMax) dataMax = v;
            maxScale = NiceRadarMax(dataMax);
        }

        // SVG 坐标系：根据 size 选项选择 viewBox/圆心/半径，四周留标签空间。
        var sizeAttr = GetAttribute(node, "radar-size");
        var size = string.IsNullOrWhiteSpace(sizeAttr) ? RadarMarker.SizeMedium : sizeAttr.Trim();
        var (viewBox, cx, cy, r) = RadarMarker.SizeGeometry(size);
        const int gridLevels = 5;

        html.Append("<div class=\"md-radar-chart\">")
            .Append("<svg class=\"md-radar-svg\" width=\"")
            .Append(viewBox.ToString(CultureInfo.InvariantCulture))
            .Append("\" height=\"")
            .Append(viewBox.ToString(CultureInfo.InvariantCulture))
            .Append("\" viewBox=\"0 0 ")
            .Append(viewBox.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(viewBox.ToString(CultureInfo.InvariantCulture))
            .Append("\" role=\"img\"");

        if (title.Length > 0)
            html.Append(" aria-label=\"").Append(WebUtility.HtmlEncode(title)).Append('"');

        html.Append(">");

        // 同心网格多边形（从外到内，外层先画避免遮挡）。
        for (var level = gridLevels; level >= 1; level--)
        {
            var frac = (double)level / gridLevels;
            html.Append("<polygon class=\"md-radar-grid\" points=\"");
            for (var i = 0; i < axisCount; i++)
            {
                var (px, py) = RadarPoint(cx, cy, r * frac, i, axisCount);
                html.Append(px.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(',')
                    .Append(py.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(' ');
            }
            html.Append("\" />");
        }

        // 轴线（中心到各顶点）。
        for (var i = 0; i < axisCount; i++)
        {
            var (px, py) = RadarPoint(cx, cy, r, i, axisCount);
            html.Append("<line class=\"md-radar-axis\" x1=\"")
                .Append(cx.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                .Append("\" y1=\"")
                .Append(cy.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                .Append("\" x2=\"")
                .Append(px.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                .Append("\" y2=\"")
                .Append(py.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                .Append("\" />");
        }

        // 轴标签（顶点外侧）。
        for (var i = 0; i < axisCount; i++)
        {
            var (px, py) = RadarPoint(cx, cy, r + 18, i, axisCount);
            var anchor = RadarLabelAnchor(i, axisCount);
            html.Append("<text class=\"md-radar-label\" x=\"")
                .Append(px.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                .Append("\" y=\"")
                .Append(py.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                .Append("\" text-anchor=\"").Append(anchor).Append("\">")
                .Append(WebUtility.HtmlEncode(table.Axes[i].Label))
                .Append("</text>");
        }

        // 系列多边形。
        for (var s = 0; s < seriesCount; s++)
        {
            var color = RadarSeriesColors[s % RadarSeriesColors.Length];
            html.Append("<polygon class=\"md-radar-series\" fill=\"").Append(color)
                .Append("\" fill-opacity=\"").Append((fillOpacity / 100.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                .Append("\" stroke=\"").Append(color).Append("\" points=\"");
            for (var i = 0; i < axisCount; i++)
            {
                var v = table.Axes[i].Values[s];
                var frac = maxScale > 0 ? Math.Clamp(v / maxScale, 0, 1) : 0;
                var (px, py) = RadarPoint(cx, cy, r * frac, i, axisCount);
                html.Append(px.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(',')
                    .Append(py.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(' ');
            }
            html.Append("\" />");
        }

        // 数据点与可选数值标签。
        for (var s = 0; s < seriesCount; s++)
        {
            var color = RadarSeriesColors[s % RadarSeriesColors.Length];
            for (var i = 0; i < axisCount; i++)
            {
                var v = table.Axes[i].Values[s];
                var frac = maxScale > 0 ? Math.Clamp(v / maxScale, 0, 1) : 0;
                var (px, py) = RadarPoint(cx, cy, r * frac, i, axisCount);
                html.Append("<circle class=\"md-radar-dot\" cx=\"")
                    .Append(px.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                    .Append("\" cy=\"")
                    .Append(py.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                    .Append("\" r=\"3\" fill=\"").Append(color).Append("\" />");

                if (showLabels)
                {
                    // 数值标签：放在数据点沿径向外移的位置，同轴线不同系列按系列索引
                    // 递增偏移量（8 + s*5），沿径向均匀错开避免重叠。数字用系列颜色，
                    // 与对应多边形同色，用户通过颜色即可识别归属。
                    var offset = 8 + s * 5;
                    var (lx, ly) = RadarPoint(cx, cy, r * frac + offset, i, axisCount);
                    var anchor = RadarLabelAnchor(i, axisCount);
                    html.Append("<text class=\"md-radar-value\" fill=\"").Append(color)
                        .Append("\" x=\"")
                        .Append(lx.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                        .Append("\" y=\"")
                        .Append(ly.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                        .Append("\" text-anchor=\"").Append(anchor).Append("\">")
                        .Append(v.ToString("G", System.Globalization.CultureInfo.InvariantCulture))
                        .Append("</text>");
                }
            }
        }

        html.Append("</svg></div>");

        if (title.Length > 0)
        {
            html.Append("<figcaption class=\"md-radar-title\">")
                .Append(WebUtility.HtmlEncode(title))
                .Append("</figcaption>");
        }

        if (showLegend && seriesCount > 0)
        {
            html.Append("<div class=\"md-radar-legend\">");
            for (var s = 0; s < seriesCount; s++)
            {
                var color = RadarSeriesColors[s % RadarSeriesColors.Length];
                html.Append("<span class=\"md-radar-legend-item\">")
                    .Append("<span class=\"md-radar-legend-swatch\" style=\"background:").Append(color).Append("\"></span>")
                    .Append("<span class=\"md-radar-legend-name\">")
                    .Append(WebUtility.HtmlEncode(table.SeriesNames[s]))
                    .Append("</span></span>");
            }
            html.Append("</div>");
        }
    }

    // 第 i 个轴（0-based，共 n 个）在半径 rad 处的坐标；起始于正上方，顺时针分布。
    private static (double x, double y) RadarPoint(double cx, double cy, double rad, int i, int n)
    {
        var angle = -Math.PI / 2 + i * (2 * Math.PI / n);
        return (cx + rad * Math.Cos(angle), cy + rad * Math.Sin(angle));
    }

    // 轴标签的 text-anchor：右侧轴用 start，左侧用 end，上下用 middle。
    private static string RadarLabelAnchor(int i, int n)
    {
        var angle = -Math.PI / 2 + i * (2 * Math.PI / n);
        var cos = Math.Cos(angle);
        if (cos > 0.2) return "start";
        if (cos < -0.2) return "end";
        return "middle";
    }

    // 将数据最大值向上取整到 1/2/5×10^n 形式的"好看"量程上限。
    private static double NiceRadarMax(double value)
    {
        if (value <= 0) return 1;
        var exp = Math.Floor(Math.Log10(value));
        var base10 = Math.Pow(10, exp);
        var norm = value / base10;
        double nice = norm switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 5,
            _ => 10
        };
        return nice * base10;
    }

    private void RenderChildren(MarkdownNode node, StringBuilder html, List<DiagnosticMessage> diagnostics)
    {
        for (int i = 0; i < node.Children.Count; i++)
            RenderNode(node.Children[i], html, diagnostics);
    }

    /// <summary>
    /// 行内 HTML 的发射器：只按“已验证的标签名 + 已放行的属性名 + 已转义的值”
    /// 重新拼装，永不回写解析期的原始标签串。标签名/属性名在解析期已按名法校验，
    /// 值一律实体转义，因此这里不需要也不允许再做一次白名单判断。
    /// </summary>
    private void RenderInlineHtml(MarkdownNode node, StringBuilder html, List<DiagnosticMessage> diagnostics)
    {
        if (!node.Attributes.TryGetValue("html-tag", out var tag) || string.IsNullOrEmpty(tag))
        {
            RenderChildren(node, html, diagnostics);
            return;
        }

        var isVoid = node.Attributes.TryGetValue("html-void", out var voidFlag)
            && string.Equals(voidFlag, "1", StringComparison.Ordinal);

        html.Append('<').Append(tag);
        foreach (var attribute in node.Attributes)
        {
            if (string.Equals(attribute.Key, "html-tag", StringComparison.Ordinal)
                || string.Equals(attribute.Key, "html-void", StringComparison.Ordinal))
            {
                continue;
            }

            html.Append(' ')
                .Append(attribute.Key)
                .Append("=\"")
                .Append(WebUtility.HtmlEncode(attribute.Value))
                .Append('"');
        }

        html.Append('>');

        if (isVoid)
        {
            return;
        }

        RenderChildren(node, html, diagnostics);
        html.Append("</").Append(tag).Append('>');
    }

    private void RenderTightContainerChildren(MarkdownNode node, StringBuilder html, List<DiagnosticMessage> diagnostics)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == NodeType.Paragraph)
            {
                RenderChildren(child, html, diagnostics);
            }
            else
            {
                RenderNode(child, html, diagnostics);
            }
        }
    }

    private void AppendStyle(MarkdownNode node, StringBuilder html, string? extraCss = null)
    {
        // 旁路「解样式」的实验态：结构照常，内联样式不参与。
        if (_fragmentOptions.SuppressInlineStyle)
        {
            return;
        }

        var css = BuildNodeInlineCss(node, extraCss);
        if (!string.IsNullOrWhiteSpace(css))
        {
            html.Append(" style=\"").Append(WebUtility.HtmlEncode(css)).Append('"');
        }
    }

    /// <summary>
    /// 按「前者为基底、后者可覆盖」的顺序拼接两段内联 CSS。
    /// 任一为空时返回另一段原文，保证无布局定义的历史输出逐字节不变。
    /// </summary>
    private static string? MergeCss(string? baseCss, string? overrideCss)
    {
        var hasBase = !string.IsNullOrWhiteSpace(baseCss);
        var hasOverride = !string.IsNullOrWhiteSpace(overrideCss);

        return (hasBase, hasOverride) switch
        {
            // 基底声明串不带结尾分号：直接拼空格会把最后一条声明和后续 CSS 糊成一条非法声明。
            (true, true) => baseCss!.TrimEnd().EndsWith(';')
                ? baseCss + " " + overrideCss
                : baseCss + "; " + overrideCss,
            (true, false) => baseCss,
            (false, true) => overrideCss,
            _ => null,
        };
    }

    /// <summary>
    /// 节点当前样式对应的完整内联 CSS（含按块类型追加的变量/单边外边距），
    /// 与全量帧写到该节点 style 属性上的内容逐字一致。无任何声明时返回 null。
    /// </summary>
    public string? BuildNodeInlineCss(MarkdownNode node) =>
        BuildNodeInlineCss(node, BuildNodeExtraInlineCss(node));

    private string? BuildNodeInlineCss(MarkdownNode node, string? extraCss)
    {
        // 只有节点自身显式设了前景色才补 inline color:；类型/继承来的颜色只输出 --node-color。
        var emitInlineColor = !string.IsNullOrWhiteSpace(node.LocalStyle.ForegroundColor);
        var css = _cssStyleBuilder.Build(_styleResolver.Resolve(node), emitInlineColor);
        if (!string.IsNullOrWhiteSpace(extraCss))
        {
            css = string.IsNullOrWhiteSpace(css) ? extraCss.Trim() : css + " " + extraCss.Trim();
        }

        return string.IsNullOrWhiteSpace(css) ? null : css;
    }

    /// <summary>
    /// 各块在基础样式之外追加的内联声明。取值一律来自该类型解析后的模板根：
    /// 局部 CSS 直写通道与全量渲染共用同一份 <see cref="PartTemplate.ExtraCss"/>，
    /// 避免在补丁通道里再维护一份按类型的样式分派。
    /// </summary>
    private string? BuildNodeExtraInlineCss(MarkdownNode node)
    {
        var variant = ResolveTemplateVariant(node);

        ElementTemplate? template = null;
        if (_templateSelectors.TryGetValue(node.Type, out var selector))
        {
            var match = ResolveSelectorMatch(selector.Key, node);
            if (!TryGetSelectedTemplate(node.Type, match, variant, out template)
                && selector.FallbackMatch != match)
            {
                return null;
            }
        }

        template ??= _templates.TryGetValue((node.Type, variant), out var found) ? found : null;
        return template?.Root.ExtraCss?.Invoke(node);
    }

    // 虚线间隙由属性面板写入围栏 info（gap=），渲染为容器级 CSS 变量，
    // 供主轴与短支线的 repeating-linear-gradient 消费。
    private static string? BuildTimelineStyleVars(MarkdownNode node)
    {
        if (GetAttributeStatic(node, "timeline-line") != "dashed")
        {
            return null;
        }

        var gapText = GetAttributeStatic(node, "timeline-gap");
        if (int.TryParse(gapText, out var gap)
            && gap is >= TimelineMarker.MinLineGap and <= TimelineMarker.MaxLineGap)
        {
            return $"--tl-gap:{gap.ToString(CultureInfo.InvariantCulture)}px;";
        }

        return null;
    }

    private static string? GetAttributeStatic(MarkdownNode node, string key) =>
        node.Attributes.TryGetValue(key, out var value) ? value : null;

    // 先按行收集格子：图片格宽度恒按“单行最多图片数”N 等分；
    // 文本格只以 auto 宽度吸收剩余空间（必要时自动折行），绝不挤压图片尺寸。
    private static List<List<string>> CollectMatrixRows(MarkdownNode node) =>
        CollectMatrixRows(node.Text);

    private static List<List<string>> CollectMatrixRows(string? source)
    {
        var lines = (source ?? string.Empty).Replace("\r\n", "\n").Split('\n');

        var rows = new List<List<string>>();
        var currentRow = new List<string>();

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                if (currentRow.Count > 0)
                {
                    rows.Add(currentRow);
                    currentRow = new List<string>();
                }

                continue;
            }

            // 围栏开/关标记行（'''image、''' 等）不是内容格，必须跳过。
            if (trimmed.StartsWith("'''", StringComparison.Ordinal))
            {
                continue;
            }

            currentRow.AddRange(SplitMatrixCells(trimmed));
        }

        if (currentRow.Count > 0)
        {
            rows.Add(currentRow);
        }

        return rows;
    }

    private static int CountMatrixColumns(List<List<string>> rows)
    {
        var columnCount = 1;
        foreach (var row in rows)
        {
            var imageCount = 0;
            foreach (var cell in row)
            {
                if (IsSafeUrl(cell))
                {
                    imageCount++;
                }
            }

            if (imageCount > columnCount)
            {
                columnCount = imageCount;
            }
        }

        return columnCount;
    }

    // 格间距与对齐经自定义变量/单边外边距注入：--matrix-gap 贯通外层、行与图片格基准，
    // 对齐对替换型容器走 auto 外边距（见 BuildReplacedAlignment）。
    private string BuildMatrixExtraCss(MarkdownNode node, int columnCount)
    {
        var resolved = _styleResolver.Resolve(node);
        var matrixExtra = new StringBuilder("--matrix-cols:").Append(columnCount);
        if (resolved.Gap is > 0)
        {
            matrixExtra.Append("; --matrix-gap:")
                .Append(resolved.Gap.Value.ToString("0.##", CultureInfo.InvariantCulture))
                .Append("px");
        }

        var matrixAlign = BuildReplacedAlignment(resolved.TextAlign, forceBlockDisplay: false);
        if (!string.IsNullOrEmpty(matrixAlign))
        {
            matrixExtra.Append("; ").Append(matrixAlign);
        }

        return matrixExtra.ToString();
    }

    private string? ResolveReplacedAlignmentCss(MarkdownNode node, bool forceBlockDisplay)
    {
        var resolved = _styleResolver.Resolve(node);
        return BuildReplacedAlignment(resolved.TextAlign, forceBlockDisplay);
    }

    /// <summary>
    /// 替换型内容（图片 / Mermaid / 公式块 / 图片矩阵）无法用 text-align 定位，
    /// 改为写水平 auto 外边距；图片默认行内级，必须先 display:block 才生效。
    /// 片段经 extraCss 追加在主样式之后，单边 longhand 仅覆盖左右、保留显式上下外边距。
    /// </summary>
    private static string? BuildReplacedAlignment(string? textAlign, bool forceBlockDisplay)
    {
        if (string.IsNullOrWhiteSpace(textAlign))
            return null;

        var prefix = forceBlockDisplay ? "display:block; " : string.Empty;

        return textAlign.Trim().ToLowerInvariant() switch
        {
            "center" => prefix + "margin-left:auto;margin-right:auto",
            "left" => prefix + "margin-left:0;margin-right:auto",
            "right" => prefix + "margin-left:auto;margin-right:0",
            _ => null
        };
    }

    /// <summary>
    /// 渲染链判定：提示块/自定义引用块默认按 kind 用语义色驱动左边条、图标与标题；
    /// 仅当用户（或样式表）显式定义了边框色时，把同色写入语义变量，让细条、图标、
    /// 标题与用户边框保持同色；只改背景色时语义色仍按 kind 保留。
    /// </summary>
    private string? ResolveSemanticAccentCss(MarkdownNode node, string variableName)
    {
        var style = _styleResolver.Resolve(node);
        if (style is null || string.IsNullOrWhiteSpace(style.BorderColor))
        {
            return null;
        }

        return variableName + ": " + style.BorderColor.Trim() + ";";
    }

    private static void AppendNodeAttributes(MarkdownNode node, StringBuilder html)
    {
        html.Append(" data-node-id=\"").Append(WebUtility.HtmlEncode(node.Id)).Append('"');

        if (node.Range.StartLine > 0)
        {
            html.Append(" data-line-start=\"").Append(node.Range.StartLine).Append('"');
            html.Append(" data-line-end=\"")
                .Append(Math.Max(node.Range.StartLine, node.Range.EndLine)).Append('"');
        }

        if (node.Attributes.ContainsKey(MarkdownAttributes.Unclosed))
        {
            html.Append(" data-block-unclosed=\"1\"");
        }
    }

    private static string? GetAttribute(MarkdownNode node, string name) =>
        node.Attributes.TryGetValue(name, out var val) && !string.IsNullOrWhiteSpace(val) ? val : null;

    /// <summary>
    /// 属性面板写入的自定义图标为 4~6 位十六进制 Unicode 码点；
    /// 校验通过才输出，非法值回退到 kind 默认图标，避免破坏 HTML。
    /// </summary>
    private static string? TryBuildIconGlyph(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var hex = raw.Trim();
        if (hex.Length is < 4 or > 6
            || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint)
            || codePoint is < 0x20 or > 0x10FFFF
            || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
        {
            return null;
        }

        return $"&#x{hex.ToLowerInvariant()};";
    }

    /// <summary>
    /// 图片专用放行规则：在通用 scheme 白名单之外，允许 data:image/ 内联图片。
    /// 内联图片由文档自带字节，不发网络请求，也不具备脚本能力（只作为 &lt;img src&gt; 使用）；
    /// 其他 data: 仍然拒绝，链接（a href）也仍然只走通用白名单。
    /// </summary>
    private static bool IsSafeImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        var trimmed = url.Trim();
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
                && !UnsafeUriCharRegex.IsMatch(trimmed);
        }

        return IsSafeUrl(trimmed);
    }

    private static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        var trimmed = url.Trim();

        if (trimmed.StartsWith("//", StringComparison.Ordinal) || UnsafeUriCharRegex.IsMatch(trimmed))
            return false;

        if (trimmed.StartsWith('#') || trimmed.StartsWith('/') ||
            trimmed.StartsWith("./", StringComparison.Ordinal) || trimmed.StartsWith("../", StringComparison.Ordinal))
        {
            return true;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && AllowedUriSchemes.Contains(uri.Scheme);
    }

    private static void AppendEncoded(StringBuilder html, string value) =>
        html.Append(WebUtility.HtmlEncode(value));
}
