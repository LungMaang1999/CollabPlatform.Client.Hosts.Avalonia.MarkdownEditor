using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Parsing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Factories;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Matching;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;
using Markdig;
using Markdig.Parsers;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

public sealed class MarkdownParser : IMarkdownParser
{
    private readonly MarkdownParserOptions _options;
    private readonly NodeFactory _nodeFactory;
    private readonly SectionBuilder _sectionBuilder;
    private readonly INodeIdentityMatcher _identityMatcher;

    // 内置数据面的解析声明（item-marker 等）。缺省取嵌入 builtin-elements.xml；
    // 显式传入只用于测试模拟“改了内置 XML”后的解析结果变化。
    private readonly IReadOnlyList<UserBlockDefinition> _builtInDeclarations;

    // 解析运行时按用户块定义集合缓存：管线与转换器都绑定一份注册表，
    // 同一文档的每次编辑复用同一运行时，定义变化只换一个缓存键。
    private readonly ConcurrentDictionary<string, ParseRuntime> _runtimes = new(StringComparer.Ordinal);

    public MarkdownParser(
        MarkdownParserOptions options,
        NodeFactory? nodeFactory = null,
        SectionBuilder? sectionBuilder = null,
        INodeIdentityMatcher? identityMatcher = null,
        IReadOnlyList<UserBlockDefinition>? builtInDeclarations = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nodeFactory = nodeFactory ?? new NodeFactory();
        _sectionBuilder = sectionBuilder ?? new SectionBuilder(_nodeFactory);
        _identityMatcher = identityMatcher ?? new NodeIdentityMatcher();
        _builtInDeclarations = builtInDeclarations ?? BuiltInElementDefinitions.Load();

        // 配置里的用户块在构造期校验；无定义时不注册解析器，保持旧管线逐字节一致。
        GetRuntime(_options.UserBlocks);
    }

    /// <summary>一份用户块定义对应的管线/转换器/注册表，创建后只读。</summary>
    private sealed class ParseRuntime
    {
        public ParseRuntime(
            MarkdownPipeline pipeline,
            MarkdigToNodeConverter converter,
            UserBlockRegistry registry,
            IReadOnlyList<UserBlockDefinition> allDefinitions)
        {
            Pipeline = pipeline;
            Converter = converter;
            Registry = registry;
            AllDefinitions = allDefinitions;
        }

        public MarkdownPipeline Pipeline { get; }

        public MarkdigToNodeConverter Converter { get; }

        public UserBlockRegistry Registry { get; }

        /// <summary>
        /// 传入本次运行时的完整定义，含只重写内置类型结构、不参与围栏竞争的
        /// <c>applies-to</c> 定义。<see cref="Registry"/> 只保留围栏定义，不能用于回写文档。
        /// </summary>
        public IReadOnlyList<UserBlockDefinition> AllDefinitions { get; }
    }

    private ParseRuntime GetRuntime(IReadOnlyList<UserBlockDefinition>? userBlocks)
    {
        var definitions = userBlocks ?? _options.UserBlocks;
        var key = ComputeRuntimeKey(definitions);
        return _runtimes.GetOrAdd(key, _ => CreateRuntime(definitions));
    }

    /// <summary>
    /// 运行时缓存键 = 语法指纹 + 结构指纹。语法指纹按设计只折叠影响解析产物的
    /// 字段（围栏/属性/内容模型），不含 <see cref="UserBlockPart"/> 结构；而运行时
    /// 携带完整定义并会被写回文档，渲染侧从文档定义读结构。只追加结构维度，
    /// 不改 <see cref="UserBlockSyntaxFingerprint"/> 的既有语义。
    /// </summary>
    internal static string ComputeRuntimeKey(IReadOnlyList<UserBlockDefinition>? definitions)
    {
        var syntax = UserBlockSyntaxFingerprint.ComputeSet(definitions);
        if (definitions is null || definitions.Count == 0)
        {
            return syntax;
        }

        var builder = new StringBuilder(syntax.Length + definitions.Count * 96);
        builder.Append(syntax).Append("|structure[");

        // 与语法指纹一致：书写顺序不影响运行时，按 Id 排序后折叠。
        foreach (var definition in definitions.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var structure = UserBlockStructureRules.Describe(definition.Structure);
            builder.Append(definition.Id.Length).Append(':').Append(definition.Id)
                .Append('=').Append(structure.Length).Append(':').Append(structure).Append(';');
        }

        builder.Append(']');

        // 关键字集合单独进缓存键：定义改了名称/围栏族但语法指纹恰好没换维度时，
        // 旧运行时会带着旧的“可识别围栏集合”继续命中，属于静默出错。
        // 这里按族显式折叠 (围栏字符, 名称/别名)，定义关键字变化必然换运行时。
        builder.Append("|keywords[");
        foreach (var definition in definitions.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            // 多字符定义要逐个字符折叠，任一围栏字符变化都必须换运行时；
            // 别名同样进缓存键，否则改了别名仍会命中旧运行时。
            foreach (var fenceChar in definition.EffectiveFenceChars.OrderBy(item => item))
            {
                foreach (var keyword in definition.EffectiveKeywords)
                {
                    builder.Append(fenceChar).Append(':')
                        .Append(keyword.Length).Append(':').Append(keyword).Append(';');
                }
            }
        }

        builder.Append(']');
        return builder.ToString();
    }

    private ParseRuntime CreateRuntime(IReadOnlyList<UserBlockDefinition> definitions)
    {
        var pipelineBuilder = new MarkdownPipelineBuilder();
        if (_options.EnablePipeTables) pipelineBuilder.UsePipeTables();
        if (_options.EnableTaskLists) pipelineBuilder.UseTaskLists();
        if (_options.EnableFootnotes) pipelineBuilder.UseFootnotes();
        if (_options.EnableYamlFrontMatter) pipelineBuilder.UseYamlFrontMatter();
        if (_options.EnableEmphasisExtras) pipelineBuilder.UseEmphasisExtras();
        if (_options.EnableAutoLinks) pipelineBuilder.UseAutoLinks();
        if (_options.EnableMathematics)
        {
            pipelineBuilder.UseMathematics();
            pipelineBuilder.BlockParsers.Insert(0, new LatexBlockMathParser());
        }

        // 行内 parser（行内公式 / 代码段）只有这一个注册入口，与影子管线共用，次序一致。
        InlineParserRegistration.Apply(pipelineBuilder, _options);

        // 解析声明的唯一装载路径：内置数据面（builtin-elements.xml）与文档定义先合并，
        // 再同时喂给关键字表与条目标记表。内置与用户不是两套入口，差别只在合并顺序：
        // 文档定义在前（同目标同变体时优先），内置声明补足缺省。
        var declarations = MergeBuiltInDeclarations(_builtInDeclarations, definitions);

        // 围栏关键字注册表按运行时重建（不是静态查表）：内置声明打底，定义声明的首词追加在后。
        // 转换器与块解析器共用这一份，定义改了关键字必然换运行时。
        var fenceKeywords = FenceKeywordRegistry.Create(declarations);

        // 三单引号族只有一个分派解析器：关键字→目标来自数据面，目标→块类型走块工厂表。
        var primeFenceParser = new FenceDispatchParser(fenceKeywords, ResolveItemMarkers(declarations));

        pipelineBuilder.BlockParsers.Insert(0, primeFenceParser);

        // 定义在构造期校验；无定义时不注册解析器，保持旧管线逐字节一致。
        var registry = new UserBlockRegistry(definitions);

        // 围栏字符由定义数据决定，注册次序按族固定：单引号族在内置分派器之后（内置优先），
        // 代码围栏族在 Markdig FencedCodeBlockParser 之前（定义优先），其余字符最高优先。
        // 新增一个围栏字符只加定义数据，这里不新增按元素的判断。
        UserBlockParser? codeFenceParser = null;
        foreach (var fenceChar in registry.DeclaredFenceChars.Distinct())
        {
            if (fenceChar == UserBlockRegistry.PrimeFenceChar)
            {
                var primeIndex = IndexOfParser(pipelineBuilder.BlockParsers, primeFenceParser) + 1;
                pipelineBuilder.BlockParsers.Insert(primeIndex, new UserBlockParser(registry, fenceChar));
                continue;
            }

            var userBlockParser = new UserBlockParser(registry, fenceChar);
            if (fenceChar is UserBlockRegistry.BacktickFenceChar or UserBlockRegistry.TildeFenceChar)
            {
                codeFenceParser = userBlockParser;
            }

            pipelineBuilder.BlockParsers.Insert(0, userBlockParser);
        }

        // 显式契约：代码围栏族的定义解析器必须位于 FencedCodeBlockParser 之前。
        // 位移顺序若被未来的插入改动破坏，这里立即失败，而不是让定义静默退回代码块。
        if (codeFenceParser is not null)
        {
            var userIndex = IndexOfParser(pipelineBuilder.BlockParsers, codeFenceParser);
            var fencedIndex = IndexOfParser(pipelineBuilder.BlockParsers, typeof(FencedCodeBlockParser));
            if (userIndex < 0 || fencedIndex < 0 || userIndex > fencedIndex)
            {
                throw new InvalidOperationException(
                    "代码围栏族定义解析器必须先于 Markdig FencedCodeBlockParser："
                    + $"user={userIndex}, fenced={fencedIndex}。");
            }
        }

        var pipeline = pipelineBuilder.Build();

        // 转换器需要 pipeline 以递归解析时间线节点正文。
        var converter = new MarkdigToNodeConverter(
            _nodeFactory,
            registry,
            fenceKeywords,
            ResolveInlineHtmlDefinition(declarations),
            ResolveBlockHtmlDefinition(declarations));
        return new ParseRuntime(pipeline, converter, registry, definitions);
    }

    /// <summary>
    /// 行内 HTML 白名单的解析侧取值：文档定义在前、内置兜底，取首条
    /// <c>applies-to="HtmlInline"</c> 且带 <c>&lt;inline-html&gt;</c> 声明的定义。
    /// 没有声明时返回 <c>null</c>，转换器保持翻案前的“标签丢弃、保留文字”。
    /// </summary>
    internal static InlineHtmlDefinition? ResolveInlineHtmlDefinition(
        IReadOnlyList<UserBlockDefinition>? definitions)
    {
        if (definitions is null)
        {
            return null;
        }

        foreach (var definition in definitions)
        {
            if (!string.Equals(definition.AppliesTo, nameof(NodeType.HtmlInline), StringComparison.OrdinalIgnoreCase)
                || definition.RenderEnabled == false
                || definition.InlineHtml is null)
            {
                continue;
            }

            return definition.InlineHtml;
        }

        return null;
    }

    /// <summary>
    /// 块级 HTML 白名单的解析侧取值：文档定义在前、内置兜底，取首条
    /// <c>applies-to="HtmlBlock"</c> 且带 <c>&lt;block-html&gt;</c> 声明的定义。
    /// 没有声明时返回 <c>null</c>，转换器保持翻案前的“整段转义”。
    /// </summary>
    internal static InlineHtmlDefinition? ResolveBlockHtmlDefinition(
        IReadOnlyList<UserBlockDefinition>? definitions)
    {
        if (definitions is null)
        {
            return null;
        }

        foreach (var definition in definitions)
        {
            if (!string.Equals(definition.AppliesTo, nameof(NodeType.HtmlBlock), StringComparison.OrdinalIgnoreCase)
                || definition.RenderEnabled == false
                || definition.BlockHtml is null)
            {
                continue;
            }

            return definition.BlockHtml;
        }

        return null;
    }

    /// <summary>
    /// 解析侧的唯一声明来源：文档定义在前、内置数据面在后。两者走同一条装载路径，
    /// 内置不再有第二套硬编码（解析相关字段只从这条合并结果读取）。
    /// 文档定义在前意味着同目标同变体时文档定义优先；内置补足文档未覆盖的目标。
    /// </summary>
    internal static IReadOnlyList<UserBlockDefinition> MergeBuiltInDeclarations(
        IReadOnlyList<UserBlockDefinition> builtIns,
        IReadOnlyList<UserBlockDefinition>? definitions)
    {
        ArgumentNullException.ThrowIfNull(builtIns);
        if (definitions is null || definitions.Count == 0)
        {
            return builtIns;
        }

        var merged = new List<UserBlockDefinition>(definitions.Count + builtIns.Count);
        merged.AddRange(definitions);
        merged.AddRange(builtIns);
        return merged;
    }

    /// <summary>
    /// 容器条目标记来自定义声明的 <c>item-marker</c>：按定义声明的目标类型（<c>applies-to</c>）
    /// 建表，同一目标有多档定义时以 <c>default</c> 变体为准；时间线未声明时回落 <c>@@</c>
    /// （旧行为逐字节不变）。查的是数据，不按元素类型写分支。
    /// </summary>
    internal static IReadOnlyDictionary<FenceTarget, string> ResolveItemMarkers(
        IReadOnlyList<UserBlockDefinition>? definitions)
    {
        var markers = new Dictionary<FenceTarget, string>();
        var defaults = new Dictionary<FenceTarget, bool>();

        foreach (var definition in definitions ?? [])
        {
            if (definition.ItemMarker is null || definition.AppliesTo is null
                || !FenceOpenTargets.TryGetTarget(definition.AppliesTo, out var target))
            {
                continue;
            }

            var isDefault = string.Equals(
                definition.Variant ?? "default",
                "default",
                StringComparison.OrdinalIgnoreCase);
            var hasExisting = markers.ContainsKey(target);
            if (!hasExisting || (isDefault && !defaults[target]))
            {
                markers[target] = definition.ItemMarker;
                defaults[target] = isDefault;
            }
        }

        // 时间线标记的历史缺省：未声明时逐字节沿用 @@。
        markers.TryAdd(FenceTarget.Timeline, TimelineMarker.DefaultMarker);
        return markers;
    }

    private static int IndexOfParser(IList<BlockParser> parsers, BlockParser parser)
    {
        for (var i = 0; i < parsers.Count; i++)
        {
            if (ReferenceEquals(parsers[i], parser))
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOfParser(IList<BlockParser> parsers, Type parserType)
    {
        for (var i = 0; i < parsers.Count; i++)
        {
            if (parsers[i].GetType() == parserType)
            {
                return i;
            }
        }

        return -1;
    }

    public ParseResult Parse(
        string source,
        MarkdownDocument? previous = null,
        ParseMode mode = ParseMode.Editing,
        IReadOnlyList<UserBlockDefinition>? userBlocks = null,
        IReadOnlyList<UserBlockDefinition>? sourceUserBlocks = null,
        UserBlockComposition? composition = null)
    {
        source ??= string.Empty;

        // 显式定义 > previous 记录的定义 > 解析器配置；previous 是空集合时不能把它
        // 当作“无定义”，否则配置里定义的用户块会在第二次解析时消失。
        // 这里要的是“展开形态”：组合引用不能留到解析运行时。
        var definitions = userBlocks
            ?? (previous is not null && previous.EffectiveUserBlockDefinitions.Count > 0
                ? previous.EffectiveUserBlockDefinitions
                : null);
        var runtime = GetRuntime(definitions);
        var diagnostics = new List<DiagnosticMessage>();
        var root = ParseTreeCore(source, previous?.Root, diagnostics, previous?.SourceMarkdown, runtime);

        var document = previous ?? new MarkdownDocument();
        document.ReplaceParsedRoot(root, source, markChanged: mode == ParseMode.Editing);

        // 组合单元先落地：定义集合一经写入就要按新单元展开，
        // 否则 EffectiveUserBlockDefinitions 会对着旧单元算。
        if (composition is not null)
        {
            document.ApplyUserBlockComposition(composition);
        }

        // 文档记住本次解析实际使用的定义，后续编辑原样带上。必须用调用方本次
        // 传入的定义，不能用缓存运行时的定义：运行时按语法+结构命中，但写回
        // 陈旧定义会让“只改结构”在重开文档时被静默回退。
        // sourceUserBlocks 是“来源形态”：装载期从这里保留 ref，落盘不写展开结果。
        document.ApplyUserBlockDefinitions(sourceUserBlocks ?? definitions ?? runtime.AllDefinitions);

        if (mode == ParseMode.Loading)
        {
            // 重开文档包：文件里不为“定义默认”单独落盘，按“值 = 定义默认”反推来源（近似，见方法注释）。
            document.InferAttributeSourcesFromDefinitions();
        }

        // 显式声明了 DefaultValue 的定义默认注入生效属性，渲染层只读 Attributes 也能拿到；
        // 没有声明的键保持缺席，继续走渲染器内置默认。
        document.ApplyDeclaredAttributeDefaults();

        if (mode == ParseMode.Loading)
        {
            document.MarkSaved();
        }

        return new ParseResult(document, diagnostics);
    }

    public MarkdownNode ParseTree(
        string source,
        MarkdownNode? previousRoot,
        string? previousSource = null,
        IReadOnlyList<UserBlockDefinition>? userBlocks = null)
    {
        source ??= string.Empty;
        var diagnostics = new List<DiagnosticMessage>();
        return ParseTreeCore(source, previousRoot, diagnostics, previousSource, GetRuntime(userBlocks));
    }

    /// <summary>
    /// 定义变更后的定向重解析：只重解析源码里出现“变化定义”围栏、或旧树里
    /// 带变化 custom-kind 的顶层块，其余块按引用复用。
    /// </summary>
    public MarkdownNode ReparseForUserBlockChange(
        string source,
        MarkdownNode previousRoot,
        UserBlockSyntaxChange change)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(previousRoot);
        ArgumentNullException.ThrowIfNull(change);

        var runtime = GetRuntime(change.Definitions);
        if (!change.HasChanges)
        {
            return previousRoot;
        }

        var previousBlocks = FlattenConcreteBlocks(previousRoot);
        if (previousBlocks.Count == 0)
        {
            return previousRoot;
        }

        var changedDefinitions = change.Definitions
            .Where(definition => change.ChangedIds.Contains(definition.Id))
            .ToList();

        // 首词分派声明一变，旧树里“本来不是块”的行可能变成块，而这些行没有旧块可失效。
        // 增量失效兜不住，只能整篇重建；否则新形态在增量解析里静默不生效。
        // 只看定义声明（opens），不针对具体元素写分支。
        if (changedDefinitions.Any(definition => !string.IsNullOrEmpty(definition.Opens)))
        {
            return FullRebuildForDefinitions(source, previousRoot, runtime);
        }

        var invalidated = new bool[previousBlocks.Count];
        for (var i = 0; i < previousBlocks.Count; i++)
        {
            invalidated[i] = ReferencesChangedDefinition(previousBlocks[i], change.ChangedIds)
                || ContainsChangedFence(source, previousBlocks[i].Range, changedDefinitions)
                || MatchesChangedAppliesTo(previousBlocks[i], changedDefinitions);
        }

        if (!invalidated.Any(flag => flag))
        {
            // 没有任何块依赖变化的定义：整棵树原样复用，连 Section 都不重建。
            return previousRoot;
        }

        var merged = new List<MarkdownNode>(previousBlocks.Count + 4);
        var priorTreeNodes = new HashSet<MarkdownNode>();

        var index = 0;
        while (index < previousBlocks.Count)
        {
            if (!invalidated[index])
            {
                merged.Add(previousBlocks[index]);
                priorTreeNodes.Add(previousBlocks[index]);
                index++;
                continue;
            }

            var last = index;
            while (last + 1 < previousBlocks.Count && invalidated[last + 1])
            {
                last++;
            }

            var sliceStart = GetLineStart(source, previousBlocks[index].Range.StartOffset);
            var sliceEnd = GetLineStartAfter(source, previousBlocks[last].Range.EndOffset);
            if (sliceStart < 0 || sliceEnd < sliceStart || sliceEnd > source.Length)
            {
                return FullRebuildForDefinitions(source, previousRoot, runtime);
            }

            var sliceBlocks = TryConvertSlice(source.Substring(sliceStart, sliceEnd - sliceStart), runtime);
            if (sliceBlocks is null)
            {
                return FullRebuildForDefinitions(source, previousRoot, runtime);
            }

            var lineDelta = CountLineBreaks(source.AsSpan(0, sliceStart));
            foreach (var block in sliceBlocks)
            {
                ShiftBlockRange(block, sliceStart, lineDelta);
            }

            merged.AddRange(sliceBlocks);
            index = last + 1;
        }

        var root = _sectionBuilder.Build(merged, priorTreeNodes);
        _identityMatcher.Match(previousRoot, root);
        return root;
    }

    public MarkdownNode UpdateUserBlockDefinitions(
        MarkdownDocument document,
        IReadOnlyList<UserBlockDefinition> definitions,
        UserBlockComposition? composition = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(definitions);

        var nextComposition = composition ?? document.Composition;

        // 定义集合存“来源形态”（ref 原文保留），解析运行时要“展开形态”（渲染器/解析器不见 ref）。
        var expanded = UserBlockCompositionRules.ExpandAll(definitions, nextComposition);

        var changedIds = new HashSet<string>(
            UserBlockSyntaxFingerprint.ComputeChangedIds(document.UserBlockDefinitions, definitions),
            StringComparer.OrdinalIgnoreCase);

        // 继承传播：父定义变化时，所有继承它的后代都要进 changedIds，
        // 否则只改父定义（围栏字符/别名/属性……）时子定义会留在旧解析树上。
        // 用新旧两套 Extends 关系做传递闭包，覆盖“父改名/换父”这些边也变了的场景。
        PropagateInheritedChanges(changedIds, document.UserBlockDefinitions, definitions);

        // 只改组合单元（片段内容/属性组）时，没有任何一条源定义的语法指纹变化，
        // 但引用者的展开结构变了。按单元本身的规范文本判一次：变了就把全部定义记为
        // 已变更，让引用者整体失效（variant 与 opens 都在“键没带依赖”上栽过）。
        // 只改定义自身的结构不走这条：那由渲染器按结构指纹重渲，不需要重解析。
        if (!string.Equals(
                UserBlockElementsSerializer.Serialize([], document.Composition),
                UserBlockElementsSerializer.Serialize([], nextComposition),
                StringComparison.Ordinal))
        {
            foreach (var definition in definitions)
            {
                changedIds.Add(definition.Id);
            }

            foreach (var definition in document.UserBlockDefinitions)
            {
                changedIds.Add(definition.Id);
            }
        }

        var change = new UserBlockSyntaxChange(expanded, changedIds);
        var root = ReparseForUserBlockChange(document.SourceMarkdown, document.Root, change);

        document.ReplaceParsedRoot(root, document.SourceMarkdown, markChanged: false);
        document.ApplyUserBlockComposition(nextComposition);
        document.ApplyUserBlockDefinitions(definitions);

        // 与编辑命令同一顺序：先按新定义补/清声明默认值，再广播变更，
        // 订阅方（已打开的编辑器）读到的就是“定义 + 默认 + 重解析”的完整状态。
        document.ApplyDeclaredAttributeDefaults();
        document.NotifyUserBlockDefinitionsChanged();
        return root;
    }

    // 继承传播：把 changedIds 沿 Extends 关系做传递闭包（新旧两套关系都算）。
    private static void PropagateInheritedChanges(
        HashSet<string> changedIds,
        IEnumerable<UserBlockDefinition>? previous,
        IEnumerable<UserBlockDefinition>? current)
    {
        if (changedIds.Count == 0) return;

        var relations = new List<(string Child, string Parent)>();
        AddRelations(previous, relations);
        AddRelations(current, relations);
        if (relations.Count == 0) return;

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (child, parent) in relations)
            {
                if (changedIds.Contains(parent) && changedIds.Add(child))
                {
                    changed = true;
                }
            }
        }

        static void AddRelations(
            IEnumerable<UserBlockDefinition>? definitions,
            List<(string Child, string Parent)> target)
        {
            if (definitions is null) return;
            foreach (var definition in definitions)
            {
                if (!string.IsNullOrEmpty(definition.Extends))
                {
                    target.Add((definition.Id, definition.Extends!));
                }
            }
        }
    }

    private MarkdownNode FullRebuildForDefinitions(
        string source,
        MarkdownNode previousRoot,
        ParseRuntime runtime)
    {
        var blocks = ParseBlocks(source, new List<DiagnosticMessage>(), runtime);
        var root = _sectionBuilder.Build(blocks);
        _identityMatcher.Match(previousRoot, root);
        return root;
    }

    // 旧树里是否有节点由变化定义产生（含被删除/改名的定义）。
    private static bool ReferencesChangedDefinition(MarkdownNode node, IReadOnlySet<string> changedIds)
    {
        if (node.Type == NodeType.Custom
            && node.Attributes.TryGetValue("custom-kind", out var kind)
            && changedIds.Contains(kind))
        {
            return true;
        }

        foreach (var child in node.Children)
        {
            if (ReferencesChangedDefinition(child, changedIds))
            {
                return true;
            }
        }

        return false;
    }

    // 定义声明的内置目标类型与块类型一致时，该块整体失效：applies-to 定义可以改变
    // 解析产物（如容器的 item-marker），不能只按 custom-kind 或围栏关键字判断。
    // 判定只看定义声明，不针对具体元素写分支。
    private static bool MatchesChangedAppliesTo(
        MarkdownNode node,
        IReadOnlyList<UserBlockDefinition> changedDefinitions)
    {
        foreach (var definition in changedDefinitions)
        {
            if (definition.AppliesTo is not null
                && string.Equals(node.Type.ToString(), definition.AppliesTo, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // 块源码里是否出现变化定义能识别（或曾经识别）的开围栏行。
    // 只扫变化定义，未变化的元素不会让任何块失效。
    private static bool ContainsChangedFence(
        string source,
        SourceRange range,
        IReadOnlyList<UserBlockDefinition> changedDefinitions)
    {
        if (changedDefinitions.Count == 0 || range.StartOffset < 0)
        {
            return false;
        }

        var start = Math.Clamp(range.StartOffset, 0, source.Length);
        var end = Math.Clamp(range.EndOffset, start, source.Length);

        var pos = start;
        while (pos <= end && pos < source.Length)
        {
            var lineEnd = source.IndexOf('\n', pos);
            if (lineEnd < 0 || lineEnd > end) lineEnd = end;

            if (TryReadUserFenceName(source, pos, lineEnd, out var fenceChar, out var fenceLength, out var name))
            {
                foreach (var definition in changedDefinitions)
                {
                    // 同名跨族互不影响：围栏字符必须在该定义声明的集合内才算命中；
                    // 别名与本名同等参与失效判定（新增别名也要让旧块重扫）。
                    if (definition.UsesFenceChar(fenceChar)
                        && fenceLength >= definition.FenceLength
                        && definition.MatchesKeyword(name))
                    {
                        return true;
                    }
                }
            }

            if (lineEnd >= end) break;
            pos = lineEnd + 1;
        }

        return false;
    }

    // 与 UserBlockParser 相同的开围栏读取：最多三空格缩进 + 定义声明的围栏符 + 名称整词。
    // 围栏符可声明，这里只做“是否可能是围栏符”的粗筛，具体归属由变化定义逐条判定。
    private static bool TryReadUserFenceName(
        string source,
        int lineStart,
        int lineEnd,
        out char fenceChar,
        out int fenceLength,
        out string name)
    {
        fenceChar = '\0';
        fenceLength = 0;
        name = string.Empty;

        var i = lineStart;
        var spaces = 0;
        while (i < lineEnd && source[i] == ' ' && spaces < 3)
        {
            i++;
            spaces++;
        }

        if (i >= lineEnd || !UserBlockRegistry.IsSupportedFenceChar(source[i]))
        {
            return false;
        }

        fenceChar = source[i];
        var fenceStart = i;
        while (i < lineEnd && source[i] == fenceChar)
        {
            i++;
        }

        fenceLength = i - fenceStart;
        if (fenceLength < UserBlockRegistry.MinFenceLength) return false;

        while (i < lineEnd && (source[i] == ' ' || source[i] == '\t')) i++;

        var nameStart = i;
        while (i < lineEnd && !char.IsWhiteSpace(source[i])) i++;
        if (i == nameStart) return false;

        name = source.Substring(nameStart, i - nameStart);
        return true;
    }

    private MarkdownNode ParseTreeCore(
        string source,
        MarkdownNode? previousRoot,
        ICollection<DiagnosticMessage> diagnostics,
        string? previousSource,
        ParseRuntime runtime)
    {
        if (previousRoot is not null
            && TryParseAppendedSuffix(source, previousSource, previousRoot, diagnostics, runtime, out var incrementalRoot))
        {
            return incrementalRoot;
        }

        if (previousRoot is not null
            && TryParseModifiedRegion(source, previousSource, previousRoot, diagnostics, runtime, out incrementalRoot))
        {
            return incrementalRoot;
        }

        var blocks = ParseBlocks(source, diagnostics, runtime);
        var root = _sectionBuilder.Build(blocks);

        if (previousRoot is not null)
        {
            // Matcher 只写新树，previousRoot 保持只读。
            _identityMatcher.Match(previousRoot, root);
        }

        return root;
    }

    // 链接引用定义 [id]: 与脚注定义 [^id]:（含冒号前可选空白）。
    private static readonly Regex ReferenceDefinitionLineRegex = new(
        @"^[ \t]{0,3}\[[^\]\n]+\][ \t]*:",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// 严格前缀追加的增量解析：前缀块直接复用上一棵树的对象，只解析追加部分，
    /// 平移 Range 后重建章节。任何不确定场景返回 false 走全量解析。
    /// </summary>
    private bool TryParseAppendedSuffix(
        string source,
        string? previousSource,
        MarkdownNode previousRoot,
        ICollection<DiagnosticMessage> diagnostics,
        ParseRuntime runtime,
        out MarkdownNode root)
    {
        root = null!;

        if (string.IsNullOrEmpty(previousSource)
            || source.Length <= previousSource.Length
            || !source.StartsWith(previousSource, StringComparison.Ordinal))
        {
            return false;
        }

        var appendOffset = previousSource.Length;

        var previousBlocks = FlattenConcreteBlocks(previousRoot);
        if (previousBlocks.Count == 0)
        {
            return false;
        }

        var cutBlockIndex = -1;
        for (var i = 0; i < previousBlocks.Count; i++)
        {
            var range = previousBlocks[i].Range;
            if (range.StartOffset >= 0
                && range.StartOffset <= appendOffset
                && appendOffset <= range.EndOffset)
            {
                cutBlockIndex = i;
                break;
            }
        }

        // 追加点落在尾部空行：从最后一个块开始重解析。
        if (cutBlockIndex < 0)
        {
            cutBlockIndex = previousBlocks.Count - 1;
        }

        // 从受影响块向前回退寻找“密封边界”：探测首块与锚点类型/RawMarkdown
        // 一致则前方前缀可安全复用；否则追加与锚点发生回溯性合并，继续扩大重解析区。
        const int maxWalkBack = 8;
        var lowest = Math.Max(1, cutBlockIndex - maxWalkBack + 1);

        // previousSource 在回退循环中不变，引用定义扫描只做一次。
        var previousHasReferenceDef = ReferenceDefinitionLineRegex.IsMatch(previousSource);

        for (var k = cutBlockIndex; k >= lowest; k--)
        {
            var anchor = previousBlocks[k];
            if (anchor.Range.StartOffset < 0 || anchor.Range.Length <= 0)
            {
                return false;
            }

            var start = GetLineStart(source, anchor.Range.StartOffset);
            if (start <= 0)
            {
                return false;
            }

            var prefixOrdered = true;
            for (var i = 0; i < k; i++)
            {
                if (previousBlocks[i].Range.EndOffset > start)
                {
                    prefixOrdered = false;
                    break;
                }
            }
            if (!prefixOrdered)
            {
                // Range 与切点交错说明结构假设不成立，保守全量。
                return false;
            }

            var probeText = source.Substring(start);

            // 引用定义是文档级解析：重解析区出现 [ 或区内新定义可能影响区外，
            // 区域无法自给自足时扩大区域，最终走全量。
            if (previousHasReferenceDef && probeText.IndexOf('[') >= 0)
            {
                continue;
            }
            if (ReferenceDefinitionLineRegex.IsMatch(probeText)
                && source.AsSpan(0, start).Contains('['))
            {
                continue;
            }

            List<MarkdownNode> probeBlocks;
            try
            {
                // 探测块 Range 为局部坐标，选中边界后统一平移；解析侧产物按探测文本判定。
                probeBlocks = ParsedDocumentPipeline.ParseAndConvert(
                    probeText,
                    runtime.Pipeline,
                    runtime.Converter,
                    runtime.Registry,
                    new List<DiagnosticMessage>());
            }
            catch
            {
                return false;
            }

            if (probeBlocks.Count == 0)
            {
                continue;
            }

            var first = probeBlocks[0];
            var sealedBoundary = first.Type == anchor.Type
                && string.Equals(first.RawMarkdown, anchor.RawMarkdown, StringComparison.Ordinal);

            if (!sealedBoundary)
            {
                continue;
            }

            // 优先在旧 Section 树上做尾部拼接；失败再退回拍平全建。
            if (TrySpliceAppendedTail(
                    source, previousRoot, previousBlocks, k, probeBlocks, start, out root))
            {
                return true;
            }

            // splice 不修改共享前缀；拍平重建抛异常则返回 false，不发布半成品树。
            try
            {
                var prefixBlocks = new List<MarkdownNode>(k);
                for (var i = 0; i < k; i++)
                {
                    prefixBlocks.Add(previousBlocks[i]);
                }

                var lineDelta = CountLineBreaks(source.AsSpan(0, start));
                foreach (var block in probeBlocks)
                {
                    ShiftBlockRange(block, start, lineDelta);
                }

                var mergedBlocks = new List<MarkdownNode>(prefixBlocks.Count + probeBlocks.Count);
                mergedBlocks.AddRange(prefixBlocks);
                mergedBlocks.AddRange(probeBlocks);

                // 前缀块是旧树共享对象，Parent 延迟到提交换根时切换。
                root = _sectionBuilder.Build(mergedBlocks, new HashSet<MarkdownNode>(prefixBlocks));
                _identityMatcher.Match(previousRoot, root);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 严格追加的“尾部 Section 拼接”：复用锚点 Section 链之前的对象（共享节点
    /// Parent 延迟切换，见 MarkdownNode.PendingParentLinks），探测块挂到复制的
    /// Section 外壳，身份匹配只在各层尾部子序列间进行。结构不成立返回 false。
    /// </summary>
    private bool TrySpliceAppendedTail(
        string source,
        MarkdownNode previousRoot,
        IReadOnlyList<MarkdownNode> previousBlocks,
        int anchorIndex,
        List<MarkdownNode> probeBlocks,
        int probeStart,
        out MarkdownNode root)
    {
        root = null!;

        try
        {
            var anchor = previousBlocks[anchorIndex];
            var anchorSection = anchor.Parent;
            if (anchorSection is null || anchorSection.Type != NodeType.Section)
            {
                return false;
            }

            // 自合成根 Section（level 0）到锚点直接父 Section 的链。
            var chain = new List<MarkdownNode>();
            for (var cur = anchorSection; cur is not null; cur = cur.Parent)
            {
                if (cur.Type != NodeType.Section) return false;
                chain.Add(cur);
                if (cur.Level == 0) break;
            }

            chain.Reverse();
            if (chain.Count == 0 || chain[0].Level != 0 || chain[0].Parent != previousRoot)
            {
                return false;
            }

            var depth = chain.Count;
            var childIndex = new int[depth - 1];
            for (var f = 0; f < depth - 1; f++)
            {
                childIndex[f] = chain[f].Children.IndexOf(chain[f + 1]);
                if (childIndex[f] < 0) return false;
            }

            var anchorIndexInSection = chain[^1].Children.IndexOf(anchor);
            if (anchorIndexInSection < 0) return false;

            // 探测块 Range 平移必须推迟到全部结构校验之后：提前 false 返回
            // 会走拍平回退，回退自己负责平移，先平移会导致二次平移。
            var lineDelta = CountLineBreaks(source.AsSpan(0, probeStart));

            // 逐层复制 Section 外壳，锚点之前的子节点共享旧引用；共享节点
            // 构建期不覆写 Parent，边记入延迟账本，提交换根时统一切换。
            var pendingParentLinks = new List<(MarkdownNode Child, MarkdownNode NewParent)>();
            var document = _nodeFactory.CreateDocument();
            document.Id = previousRoot.Id;

            var shells = new MarkdownNode[depth];
            var extras = new List<MarkdownNode>[depth];
            for (var f = 0; f < depth; f++)
            {
                shells[f] = _nodeFactory.CreateSection(chain[f].Level ?? 0);
                shells[f].Text = chain[f].Text;
                extras[f] = new List<MarkdownNode>();

                var prefixCount = f == depth - 1 ? anchorIndexInSection : childIndex[f];
                for (var ci = 0; ci < prefixCount; ci++)
                {
                    shells[f].AttachSharedChildDeferred(chain[f].Children[ci], pendingParentLinks);
                }
            }

            for (var f = 0; f < depth - 1; f++)
            {
                shells[f].AttachChildFromPriorTree(shells[f + 1]);
            }

            document.AttachChildFromPriorTree(shells[0]);

            // 与 SectionBuilder 完全相同的栈逻辑处理探测块（含新标题弹栈）。
            var shellSet = new HashSet<MarkdownNode>(shells);
            var frameOf = new Dictionary<MarkdownNode, int>(depth);
            for (var f = 0; f < depth; f++) frameOf[shells[f]] = f;

            // Stack 按枚举顺序压栈：正序传入使最深壳位于栈顶。
            var stack = new Stack<MarkdownNode>(shells);
            var poppedShells = new HashSet<MarkdownNode>();

            foreach (var node in probeBlocks)
            {
                if (node.Type != NodeType.Heading)
                {
                    var top = stack.Peek();
                    top.AttachChildFromPriorTree(node);
                    if (frameOf.TryGetValue(top, out var frame)) extras[frame].Add(node);
                    continue;
                }

                var level = Math.Max(1, node.Level ?? 1);
                while (stack.Count > 1 && (stack.Peek().Level ?? 0) >= level)
                {
                    var leaving = stack.Pop();
                    if (shellSet.Contains(leaving)) poppedShells.Add(leaving);
                }

                var section = _nodeFactory.CreateSection(level);
                section.Text = node.Text;
                section.AttachChildFromPriorTree(node);
                var parent = stack.Peek();
                parent.AttachChildFromPriorTree(section);
                if (frameOf.TryGetValue(parent, out var parentFrame))
                {
                    extras[parentFrame].Add(section);
                }

                stack.Push(section);
            }

            // 弹栈后为空的种子壳（如锚点本身是标题）必须移除：全量 Build
            // 不产出空 Section，且空壳不能承接旧 Section Id。
            var removedShells = new HashSet<MarkdownNode>();
            foreach (var dead in poppedShells)
            {
                if (dead.Children.Count == 0 && dead.Parent is not null && shellSet.Contains(dead.Parent))
                {
                    dead.Parent.Children.Remove(dead);
                    removedShells.Add(dead);
                }
            }

            // 保留下来的种子壳（含非空闭合壳）与旧链 Section 一一对应，承接其 Id。
            for (var f = 0; f < depth; f++)
            {
                if (!removedShells.Contains(shells[f])) shells[f].Id = chain[f].Id;
            }

            // 逐层只匹配旧/新尾部子序列：最深层旧尾部从锚点起；上层从路径
            // 子节点之后起，路径壳被空弹移除时则把它纳入旧尾部承接身份。
            for (var f = 0; f < depth; f++)
            {
                if (removedShells.Contains(shells[f])) continue;

                int oldStart;
                if (f == depth - 1)
                {
                    oldStart = anchorIndexInSection;
                }
                else
                {
                    oldStart = childIndex[f] + (removedShells.Contains(shells[f + 1]) ? 0 : 1);
                }

                var oldChildren = chain[f].Children;
                if (oldStart >= oldChildren.Count || extras[f].Count == 0) continue;

                var oldTail = oldChildren.GetRange(oldStart, oldChildren.Count - oldStart);
                _identityMatcher.MatchSiblingLists(oldTail, extras[f]);
            }

            // 结构前提成立：探测块局部坐标平移到全文坐标。
            foreach (var block in probeBlocks)
            {
                ShiftBlockRange(block, probeStart, lineDelta);
            }

            if (pendingParentLinks.Count > 0)
            {
                document.PendingParentLinks = pendingParentLinks;
            }

            root = document;
            return true;
        }
        catch
        {
            // 交回调用方走拍平全建路径。
            root = null!;
            return false;
        }
    }

    /// <summary>
    /// 中间编辑（插入/删除/替换）的增量解析：前缀块复用，改动区间顶层块重解析，
    /// 后缀块复用或深克隆平移 Range；两侧做密封探测，不密封则扩区（最多各 8 块）。
    /// 不确定场景返回 false 走全量。
    /// </summary>
    private bool TryParseModifiedRegion(
        string source,
        string? previousSource,
        MarkdownNode previousRoot,
        ICollection<DiagnosticMessage> diagnostics,
        ParseRuntime runtime,
        out MarkdownNode root)
    {
        root = null!;

        if (string.IsNullOrEmpty(previousSource) || ReferenceEquals(source, previousSource))
        {
            return false;
        }

        var oldLen = previousSource.Length;
        var newLen = source.Length;

        var p = 0;
        var maxPrefix = Math.Min(oldLen, newLen);
        while (p < maxPrefix && previousSource[p] == source[p])
        {
            p++;
        }

        var cs = 0;
        while (cs < oldLen - p
            && cs < newLen - p
            && previousSource[oldLen - 1 - cs] == source[newLen - 1 - cs])
        {
            cs++;
        }

        // 纯追加由 TryParseAppendedSuffix 负责；前后缀都为空（整文档替换）不值得增量。
        if (p == oldLen || (p == 0 && cs == 0))
        {
            return false;
        }

        var oldBlocks = FlattenConcreteBlocks(previousRoot);
        if (oldBlocks.Count == 0)
        {
            return false;
        }

        // 改动区间（旧坐标 [a,bOld)，新坐标 [a,bNew)），先按行边界对齐。
        var a = GetLineStart(source, Math.Min(p, newLen));

        // 公共后缀必须锁步推进到同一个行边界，否则两侧各自对齐会落到不同行，
        // bNew-bOld 不再等于整体 delta。
        var bOld = oldLen - cs;
        var bNew = newLen - cs;
        while (true)
        {
            var oldAtLineStart = bOld == 0 || previousSource[bOld - 1] == '\n';
            var newAtLineStart = bNew == 0 || source[bNew - 1] == '\n';
            if (oldAtLineStart && newAtLineStart) break;
            if (bOld >= oldLen || bNew >= newLen)
            {
                // 公共后缀内找不到共同行边界，保守全量。
                return false;
            }
            bOld++;
            bNew++;
        }

        // 边界停在空行行首时吞掉所有连续空行：容器 span 与未闭合围栏会吞并
        // 尾部换行，空行必须放进中间重解析区。
        bOld = ConsumeBlankLines(previousSource, bOld);
        bNew = ConsumeBlankLines(source, bNew);
        var delta = bNew - bOld;

        var i0 = -1;
        var i1 = -1;
        for (var i = 0; i < oldBlocks.Count; i++)
        {
            var range = oldBlocks[i].Range;
            if (range.StartOffset < 0)
            {
                return false;
            }
            if (range.StartOffset < bOld && range.EndOffset > a)
            {
                if (i0 < 0) i0 = i;
                i1 = i;
            }
        }

        int leftCount;
        int suffixStart;

        if (i0 >= 0)
        {
            // 切点落在多行块内部时向左扩到整块行首；a 不能越过分歧点，
            // 否则新源码行首字符会被漏掉。
            leftCount = i0;
            suffixStart = i1 + 1;
            a = Math.Min(a, GetLineStart(previousSource, oldBlocks[i0].Range.StartOffset));
            var alignedBOld = bOld;
            var blockEnd = GetLineStartAfter(previousSource, oldBlocks[i1].Range.EndOffset);
            bOld = Math.Max(alignedBOld, blockEnd);
            bOld = ConsumeBlankLines(previousSource, bOld);
            bNew = bOld + delta;
        }
        else
        {
            // 编辑完全落在块间空行缝隙：中间块数为 0，前缀/后缀直接拼接。
            leftCount = 0;
            foreach (var block in oldBlocks)
            {
                if (block.Range.EndOffset <= a) leftCount++;
            }
            suffixStart = leftCount;
        }

        for (var i = 0; i < leftCount; i++)
        {
            if (oldBlocks[i].Range.EndOffset > a) return false;
        }
        for (var i = suffixStart; i < oldBlocks.Count; i++)
        {
            if (oldBlocks[i].Range.StartOffset < bOld) return false;
        }

        // 未闭合围栏一路吃到 EOF，其后任何编辑都可能改变跨度，直接全量。
        // 统一按 unclosed 属性判断，新增围栏类型无需再维护类型清单。
        if (ContainsUnclosedFence(oldBlocks))
        {
            return false;
        }

        const int maxExpand = 8;
        List<MarkdownNode>? middleBlocks = null;

        for (var attempt = 0; attempt <= maxExpand; attempt++)
        {
            if (a < 0 || bNew < a || bNew > newLen || bOld > oldLen)
            {
                return false;
            }

            // 后缀锚点必须全部位于边界之后；扩区后该不变量被破坏则全量。
            if (bOld >= oldLen && suffixStart < oldBlocks.Count)
            {
                return false;
            }
            for (var i = suffixStart; i < oldBlocks.Count; i++)
            {
                if (oldBlocks[i].Range.StartOffset < bOld) return false;
            }

            middleBlocks = TryConvertSlice(source.Substring(a, bNew - a), runtime);
            if (middleBlocks is null)
            {
                return false;
            }

            // 中间切片未闭合且其后还有正文时，闭合行可能在后缀探测窗口之外，全量。
            if (bNew < newLen && ContainsUnclosedFence(middleBlocks))
            {
                return false;
            }

            var currentSuffixOffsetDelta = bNew - bOld;
            var currentSuffixLineDelta = CountLineBreaks(source.AsSpan(0, bNew))
                - CountLineBreaks(previousSource.AsSpan(0, bOld));
            var middleStartLine = CountLineBreaks(source.AsSpan(0, a)) + 1;

            // 左密封：探测切片从锚点前一块开始覆盖中间区（中间 0 块时带上
            // 右侧首块），发现块吞并、内部结构变化或 span 伸缩。
            var leftSealed = true;
            if (a > 0 && leftCount > 0)
            {
                var expected = new List<ExpectedBlock>();
                var firstExpected = leftCount - 1;
                if (leftCount >= 2) firstExpected = leftCount - 2;
                for (var i = firstExpected; i < leftCount; i++)
                {
                    var n = oldBlocks[i];
                    expected.Add(new ExpectedBlock(n, n.Range.StartOffset, n.Range.StartLine, n.Range.EndLine));
                }

                var probeStart = GetLineStart(previousSource, oldBlocks[firstExpected].Range.StartOffset);
                var probeEnd = bNew;
                if (middleBlocks.Count == 0 && suffixStart < oldBlocks.Count)
                {
                    probeEnd = GetLineStartAfter(
                        source, oldBlocks[suffixStart].Range.EndOffset + delta);
                    expected.Add(new ExpectedBlock(
                        oldBlocks[suffixStart],
                        oldBlocks[suffixStart].Range.StartOffset + currentSuffixOffsetDelta,
                        oldBlocks[suffixStart].Range.StartLine + currentSuffixLineDelta,
                        oldBlocks[suffixStart].Range.EndLine + currentSuffixLineDelta));
                }

                var probe = TryConvertSlice(source.Substring(probeStart, probeEnd - probeStart), runtime);
                if (probe is null)
                {
                    leftSealed = false;
                }
                else
                {
                    var probeStartLine = CountLineBreaks(source.AsSpan(0, probeStart)) + 1;
                    leftSealed = BlocksPrefixEqual(probe, expected, probeStart, probeStartLine);

                    // 探测切片带外侧上下文重解析了整个中间区：中间块还须与
                    // 独立切片逐块一致，防止末块 span 吞/漏尾换行。
                    if (leftSealed && middleBlocks.Count > 0)
                    {
                        leftSealed = MiddleMatchesProbe(
                            probe, middleBlocks, expected.Count,
                            probe.Count - expected.Count,
                            probeStart, probeStartLine, a, middleStartLine);
                    }
                }
            }

            // 右密封对称：探测切片延伸到锚点后一块；中间 0 块时起点回退到左侧末块。
            var rightSealed = true;
            if (bOld < oldLen && suffixStart < oldBlocks.Count)
            {
                var expected = new List<ExpectedBlock>();
                var anchorEndOld = GetLineStartAfter(previousSource, oldBlocks[suffixStart].Range.EndOffset);
                var anchorEndNew = anchorEndOld + delta;
                var probeStart = a;
                var probeEnd = anchorEndNew;

                if (middleBlocks.Count == 0 && leftCount > 0)
                {
                    probeStart = GetLineStart(source, oldBlocks[leftCount - 1].Range.StartOffset);
                    var pre = oldBlocks[leftCount - 1];
                    expected.Add(new ExpectedBlock(pre, pre.Range.StartOffset, pre.Range.StartLine, pre.Range.EndLine));
                }

                var suffixEntryCount = 0;
                for (var i = suffixStart; i <= Math.Min(suffixStart + 1, oldBlocks.Count - 1); i++)
                {
                    var n = oldBlocks[i];
                    expected.Add(new ExpectedBlock(
                        n,
                        n.Range.StartOffset + currentSuffixOffsetDelta,
                        n.Range.StartLine + currentSuffixLineDelta,
                        n.Range.EndLine + currentSuffixLineDelta));
                    suffixEntryCount++;
                    if (i == suffixStart + 1)
                    {
                        probeEnd = GetLineStartAfter(source, n.Range.EndOffset + delta);
                    }
                }

                var probe = TryConvertSlice(source.Substring(probeStart, probeEnd - probeStart), runtime);
                if (probe is null)
                {
                    rightSealed = false;
                }
                else
                {
                    var probeStartLine = CountLineBreaks(source.AsSpan(0, probeStart)) + 1;
                    rightSealed = BlocksSuffixEqual(probe, expected, probeStart, probeStartLine);

                    if (rightSealed && middleBlocks.Count > 0)
                    {
                        rightSealed = MiddleMatchesProbe(
                            probe, middleBlocks, 0,
                            probe.Count - suffixEntryCount,
                            probeStart, probeStartLine, a, middleStartLine);
                    }
                }
            }

            if (leftSealed && rightSealed)
            {
                break;
            }

            // 中间 0 块时须联合扩区，否则同一块会既在中间体里又留在后缀里。
            var expandLeft = false;
            var expandRight = false;
            if (!leftSealed)
            {
                if (leftCount == 0) return false;
                expandLeft = true;
                if (middleBlocks.Count == 0 && suffixStart < oldBlocks.Count)
                {
                    expandRight = true;
                }
            }
            else if (!rightSealed)
            {
                if (suffixStart >= oldBlocks.Count) return false;
                expandRight = true;
                if (middleBlocks.Count == 0 && leftCount > 0)
                {
                    expandLeft = true;
                }
            }

            if (expandLeft)
            {
                leftCount--;
                a = GetLineStart(previousSource, oldBlocks[leftCount].Range.StartOffset);
            }
            if (expandRight)
            {
                bOld = GetLineStartAfter(previousSource, oldBlocks[suffixStart].Range.EndOffset);
                bOld = ConsumeBlankLines(previousSource, bOld);
                bNew = bOld + delta;
                suffixStart++;
            }
            if (attempt == maxExpand)
            {
                return false;
            }
        }

        if (middleBlocks is null)
        {
            return false;
        }

        var middleText = source.Substring(a, bNew - a);

        // 引用定义是文档级解析：改动区内外交叉出现定义与 [ 用法时放弃增量。
        var outsidePrefix = source.AsSpan(0, a);
        var outsideSuffix = source.AsSpan(bNew);
        var defInMiddle = ReferenceDefinitionLineRegex.IsMatch(middleText);
        var defOutside = ReferenceDefinitionLineRegex.IsMatch(outsidePrefix)
            || ReferenceDefinitionLineRegex.IsMatch(outsideSuffix);
        var bracketInMiddle = middleText.IndexOf('[') >= 0;
        var bracketOutside = outsidePrefix.Contains('[') || outsideSuffix.Contains('[');
        if ((defInMiddle && bracketOutside) || (defOutside && bracketInMiddle))
        {
            return false;
        }

        var merged = new List<MarkdownNode>(oldBlocks.Count + middleBlocks.Count + 1);

        for (var i = 0; i < leftCount; i++)
        {
            merged.Add(oldBlocks[i]);
        }

        var middleLineDelta = CountLineBreaks(source.AsSpan(0, a));
        foreach (var block in middleBlocks)
        {
            ShiftBlockRange(block, a, middleLineDelta);
            merged.Add(block);
        }

        // 后缀偏移/行号增量均为 0（等长等行数替换）时直接复用旧对象；
        // 否则单趟完成深克隆 + 坐标平移（旧树只读，不能原地改坐标）。
        var suffixOffsetDelta = bNew - bOld;
        var suffixLineDelta = CountLineBreaks(source.AsSpan(0, bNew))
            - CountLineBreaks(previousSource.AsSpan(0, bOld));
        var suffixUnchanged = suffixOffsetDelta == 0 && suffixLineDelta == 0;
        for (var i = suffixStart; i < oldBlocks.Count; i++)
        {
            merged.Add(suffixUnchanged
                ? oldBlocks[i]
                : CloneAndShiftBlock(oldBlocks[i], suffixOffsetDelta, suffixLineDelta));
        }

        // 前缀块与等长后缀块都是旧树共享对象，Parent 延迟到提交时切换。
        var priorTreeNodes = new HashSet<MarkdownNode>();
        for (var i = 0; i < leftCount; i++)
        {
            priorTreeNodes.Add(oldBlocks[i]);
        }

        if (suffixUnchanged)
        {
            for (var i = suffixStart; i < oldBlocks.Count; i++)
            {
                priorTreeNodes.Add(oldBlocks[i]);
            }
        }

        root = _sectionBuilder.Build(merged, priorTreeNodes);
        _identityMatcher.Match(previousRoot, root);
        return true;
    }

    private List<MarkdownNode>? TryConvertSlice(string text, ParseRuntime runtime)
    {
        try
        {
            return ParsedDocumentPipeline.ParseAndConvert(
                text,
                runtime.Pipeline,
                runtime.Converter,
                runtime.Registry,
                new List<DiagnosticMessage>());
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct ExpectedBlock(
        MarkdownNode Node, int NewStart, int NewStartLine, int NewEndLine);

    // 探测切片中的“中间部分”必须与独立中间切片逐块结构/全局坐标一致。
    private static bool MiddleMatchesProbe(
        IReadOnlyList<MarkdownNode> probe,
        IReadOnlyList<MarkdownNode> middle,
        int probeIndex,
        int count,
        int probeStart,
        int probeStartLine,
        int middleStart,
        int middleStartLine)
    {
        if (count != middle.Count || probeIndex + count > probe.Count) return false;

        for (var i = 0; i < count; i++)
        {
            var m = middle[i];
            var expected = new ExpectedBlock(
                m,
                middleStart + m.Range.StartOffset,
                middleStartLine + m.Range.StartLine - 1,
                middleStartLine + m.Range.EndLine - 1);
            if (!BlockSubtreeEqual(expected, probe[probeIndex + i], probeStart, probeStartLine))
            {
                return false;
            }
        }

        return true;
    }

    // 探测切片的开头是否依次出现期望的旧块（结构与相对坐标完全一致，忽略 Id/样式）。
    private static bool BlocksPrefixEqual(
        IReadOnlyList<MarkdownNode> probe,
        IReadOnlyList<ExpectedBlock> expected,
        int sliceStart,
        int sliceStartLine)
    {
        if (probe.Count < expected.Count) return false;
        for (var i = 0; i < expected.Count; i++)
        {
            if (!BlockSubtreeEqual(expected[i], probe[i], sliceStart, sliceStartLine)) return false;
        }
        return true;
    }

    // 探测切片的结尾是否依次出现期望的旧块。
    private static bool BlocksSuffixEqual(
        IReadOnlyList<MarkdownNode> probe,
        IReadOnlyList<ExpectedBlock> expected,
        int sliceStart,
        int sliceStartLine)
    {
        if (probe.Count < expected.Count) return false;
        var offset = probe.Count - expected.Count;
        for (var i = 0; i < expected.Count; i++)
        {
            if (!BlockSubtreeEqual(expected[i], probe[offset + i], sliceStart, sliceStartLine)) return false;
        }
        return true;
    }

    // 块结构等价：类型/层级/文本/Raw/属性/全部后代一致，且切片局部坐标与
    // 期望的新文档坐标一致（不含 Id/样式）。
    private static bool BlockSubtreeEqual(
        ExpectedBlock expected,
        MarkdownNode actual,
        int sliceStart,
        int sliceStartLine)
    {
        var node = expected.Node;
        var range = actual.Range;
        var rangeMatches = range.StartOffset == expected.NewStart - sliceStart
            && range.Length == node.Range.Length
            && range.StartLine == expected.NewStartLine - sliceStartLine + 1
            && range.EndLine == expected.NewEndLine - sliceStartLine + 1
            && range.StartColumn == node.Range.StartColumn
            && range.EndColumn == node.Range.EndColumn;

        if (!rangeMatches
            || node.Type != actual.Type
            || node.Category != actual.Category
            || node.Level != actual.Level
            || !string.Equals(node.Text, actual.Text, StringComparison.Ordinal)
            || !string.Equals(node.RawMarkdown, actual.RawMarkdown, StringComparison.Ordinal)
            || node.Attributes.Count != actual.Attributes.Count
            || node.Children.Count != actual.Children.Count)
        {
            return false;
        }

        foreach (var pair in node.Attributes)
        {
            if (!actual.Attributes.TryGetValue(pair.Key, out var value)
                || !string.Equals(value, pair.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        // 后代期望坐标 = 旧坐标叠加与块首相同的整体平移量，递归沿用。
        var offsetShift = expected.NewStart - node.Range.StartOffset;
        var lineShift = expected.NewStartLine - node.Range.StartLine;
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            var expectedChild = new ExpectedBlock(
                child,
                child.Range.StartOffset + offsetShift,
                child.Range.StartLine + lineShift,
                child.Range.EndLine + lineShift);
            if (!BlockSubtreeEqual(expectedChild, actual.Children[i], sliceStart, sliceStartLine))
            {
                return false;
            }
        }

        return true;
    }

    // 递归检测块子树内是否存在未闭合围栏（含嵌套在列表/引用中的围栏）。
    // 以 unclosed 属性为准：代码/公式/图片矩阵/诗词/时间线/雷达图由解析侧
    // UnclosedBlockMarker 标记，用户自定义块由 UserBlock.Closed 标记；新增类型无需改动这里。
    private static bool ContainsUnclosedFence(IEnumerable<MarkdownNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Attributes.ContainsKey(MarkdownAttributes.Unclosed))
            {
                return true;
            }
            if (ContainsUnclosedFence(node.Children)) return true;
        }
        return false;
    }

    // 从行首 pos 起跨过所有连续的纯空白空行，返回第一个非空行行首（或 EOF）。
    private static int ConsumeBlankLines(string text, int pos)
    {
        while (pos < text.Length && IsBlankLineAt(text, pos))
        {
            pos = SkipLine(text, pos);
        }
        return pos;
    }

    // pos 是否位于一条纯空白（空格/制表符）空行的行首（含文档末尾）。
    private static bool IsBlankLineAt(string text, int pos)
    {
        if (pos >= text.Length) return true;
        var p = pos;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
        return p >= text.Length || text[p] == '\n' || text[p] == '\r';
    }

    // 返回从行首 pos 开始的下一非空行行首（跨过整行含换行符）。
    private static int SkipLine(string text, int pos)
    {
        var p = pos;
        while (p < text.Length && text[p] != '\n') p++;
        return p >= text.Length ? text.Length : p + 1;
    }

    private static int GetLineStartAfter(string text, int offset)
    {
        var pos = Math.Clamp(offset, 0, text.Length);
        while (pos < text.Length && text[pos] != '\n')
        {
            pos++;
        }
        return pos >= text.Length ? text.Length : pos + 1;
    }

    // 深克隆块子树并在同一趟递归里平移坐标，规则与 ShiftBlockRange 一致：
    // 默认空 Range 不平移；子节点“起点在父块之前”的伪坐标按轴跳过。
    private static MarkdownNode CloneAndShiftBlock(MarkdownNode node, int offsetDelta, int lineDelta)
    {
        var range = node.Range;
        SourceRange shifted = range;
        var hasRange = range.StartOffset != 0 || range.Length != 0 || range.StartLine > 1;
        if (range.StartOffset >= 0 && hasRange)
        {
            shifted = new SourceRange
            {
                StartOffset = range.StartOffset + offsetDelta,
                Length = range.Length,
                StartLine = range.StartLine + lineDelta,
                StartColumn = range.StartColumn,
                EndLine = range.EndLine + lineDelta,
                EndColumn = range.EndColumn
            };
        }

        var clone = new MarkdownNode(node.Id, node.LocalStyle)
        {
            Type = node.Type,
            Category = node.Category,
            Text = node.Text,
            RawMarkdown = node.RawMarkdown,
            Level = node.Level,
            Range = shifted,
            StyleId = node.StyleId,
            IsTableHeader = node.IsTableHeader,
            IsSynthetic = node.IsSynthetic
        };

        foreach (var pair in node.Attributes)
        {
            clone.Attributes[pair.Key] = pair.Value;
        }

        // 来源与实例回退层必须一并克隆：否则被复用的旧块会退化成“来源未知的实例值”，
        // 在重解析时错误地压过定义默认值。
        foreach (var pair in node.AttributeSources)
        {
            clone.AttributeSources[pair.Key] = pair.Value;
        }

        foreach (var pair in node.InstanceAttributes)
        {
            clone.InstanceAttributes[pair.Key] = pair.Value;
        }

        // 手动挂接：AddChild 会把孩子从旧树摘除。包含判定用原始坐标。
        foreach (var child in node.Children)
        {
            var cr = child.Range;
            var childOffsetDelta = cr.StartOffset < range.StartOffset ? 0 : offsetDelta;
            var childLineDelta = cr.StartLine < range.StartLine ? 0 : lineDelta;
            var childClone = CloneAndShiftBlock(child, childOffsetDelta, childLineDelta);
            childClone.Parent = clone;
            clone.Children.Add(childClone);
        }

        return clone;
    }

    private static List<MarkdownNode> FlattenConcreteBlocks(MarkdownNode node)
    {
        var result = new List<MarkdownNode>();
        CollectConcreteBlocks(node, result);
        return result;
    }

    private static void CollectConcreteBlocks(MarkdownNode node, List<MarkdownNode> result)
    {
        foreach (var child in node.Children)
        {
            if (child.Type is NodeType.Document or NodeType.Section)
            {
                CollectConcreteBlocks(child, result);
            }
            else
            {
                result.Add(child);
            }
        }
    }

    private static int GetLineStart(string source, int offset)
    {
        var start = Math.Clamp(offset, 0, source.Length);
        while (start > 0 && source[start - 1] != '\n')
        {
            start--;
        }
        return start;
    }

    private static int CountLineBreaks(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (c == '\n') count++;
        }
        return count;
    }

    private static void ShiftBlockRange(MarkdownNode node, int offsetDelta, int lineDelta)
    {
        var range = node.Range;
        // 默认空 Range（0:0/ln1，如空表格单元格）不平移，与全量解析保持一致。
        var hasRange = range.StartOffset != 0 || range.Length != 0 || range.StartLine > 1;
        if (range.StartOffset >= 0 && hasRange)
        {
            node.Range = new SourceRange
            {
                StartOffset = range.StartOffset + offsetDelta,
                Length = range.Length,
                StartLine = range.StartLine + lineDelta,
                StartColumn = range.StartColumn,
                EndLine = range.EndLine + lineDelta,
                EndColumn = range.EndColumn
            };
        }

        foreach (var child in node.Children)
        {
            // 跨行未闭合行内结构可能产生“起点在父块之前”的伪坐标（行号仍
            // 有效），偏移与行号分别判定；子节点超出只覆盖首行的父块是正常的。
            var cr = child.Range;
            var offsetStartsOutside = cr.StartOffset < range.StartOffset;
            var lineStartsOutside = cr.StartLine < range.StartLine;
            ShiftBlockRange(
                child,
                offsetStartsOutside ? 0 : offsetDelta,
                lineStartsOutside ? 0 : lineDelta);
        }
    }

    private IReadOnlyList<MarkdownNode> ParseBlocks(
        string source,
        ICollection<DiagnosticMessage> diagnostics,
        ParseRuntime runtime)
    {
        try
        {
            return ParsedDocumentPipeline.ParseAndConvert(
                source,
                runtime.Pipeline,
                runtime.Converter,
                runtime.Registry,
                diagnostics);
        }
        catch (ParsedDocumentException ex) when (ex.Stage == ParsedDocumentStage.Parse)
        {
            diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Error,
                Code = "MARKDOWN_PARSE_FAILED",
                Message = ex.Message,
                Range = SourceRange.Empty()
            });

            return Array.Empty<MarkdownNode>();
        }
        catch (Exception ex)
        {
            diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Error,
                Code = "MARKDOWN_CONVERSION_FAILED",
                Message = ex.Message,
                Range = SourceRange.Empty()
            });

            return Array.Empty<MarkdownNode>();
        }
    }
}
