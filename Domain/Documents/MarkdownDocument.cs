using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Validation;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

public sealed class MarkdownDocument
{
    private long _sourceRevision;
    private long _savedSourceRevision;
    private long _styleRevision;
    private long _savedStyleRevision;

    private string _sourceMarkdown = string.Empty;
    private string _savedSourceMarkdown = string.Empty;
    private string _savedStyleFingerprint = string.Empty;

    private MarkdownNode _root;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = string.Empty;

    public string SourceMarkdown => _sourceMarkdown;
    public MarkdownNode Root => _root;

    /// <summary>
    /// 当前 <see cref="Root"/> 对应的源码（最近一次解析提交时的快照）。
    /// 增量追加解析据此判断新旧源码的公共前缀；首帧/加载前为 null。
    /// </summary>
    public string? LastParsedSource { get; private set; }
    public StyleSheet StyleSheet { get; private set; } = new();
    public DocumentMetadata Metadata { get; private set; } = new();
    public EditorState EditorState { get; private set; } = new();

    /// <summary>
    /// 随文档加载的用户块定义（<c>.md.elements.xml</c>）。解析该文档的每次
    /// 编辑都必须带上同一份定义，否则自定义块会在下一次键入时消失。
    /// <para>这是<b>来源形态</b>：结构里可以保留 <c>&lt;include&gt;</c>，属性组引用保留在
    /// <see cref="UserBlockDefinition.AttributeGroups"/>。落盘写的就是它，
    /// 因此 <c>save → load → save</c> 不会把展开结果写回文件。</para>
    /// </summary>
    public IReadOnlyList<UserBlockDefinition> UserBlockDefinitions { get; private set; } = [];

    /// <summary>
    /// 定义间组合单元（属性组 + 结构片段）。与定义一起从 <c>.md.elements.xml</c> 装载，
    /// 落盘时原样写回。
    /// </summary>
    public UserBlockComposition Composition { get; private set; } = UserBlockComposition.Empty;

    private IReadOnlyList<UserBlockDefinition> _effectiveUserBlockDefinitions = [];

    /// <summary>
    /// 展开后的定义集合：渲染器与解析器只消费这一份，永远看不到 <c>ref</c>。
    /// 组合表达与手工内联表达在这里汇合为同一个对象图，因此两者的
    /// HTML 与 <c>data-block-sig</c> 必须逐字节相等。
    /// </summary>
    public IReadOnlyList<UserBlockDefinition> EffectiveUserBlockDefinitions => _effectiveUserBlockDefinitions;

    /// <summary>
    /// 随文档从 <c>.md.graph.xml</c> 装载的复合阶段声明。渲染期只读；
    /// 注册表按文档重建，声明不写进任何全局单例。
    /// </summary>
    public IReadOnlyList<CompositeStageDefinition> GraphStages { get; private set; } = [];

    /// <summary>
    /// 用户块定义（含解析产物与渲染结构）已经变更并完成定向重解析后触发。
    /// 订阅方只需要同步视图树与预览，不需要也不应该自行再解析一次。
    /// 编辑路径每次解析都复用同一份定义列表，因此不会触发。
    /// </summary>
    public event EventHandler? UserBlockDefinitionsChanged;

    /// <summary>由文档加载通道写入定义集合；空集合表示文档没有自定义元素。</summary>
    internal void ApplyUserBlockDefinitions(IReadOnlyList<UserBlockDefinition>? definitions)
    {
        UserBlockDefinitions = definitions ?? [];
        RecomputeEffectiveDefinitions();
    }

    /// <summary>由文档加载通道写入组合单元；随后重算展开后的定义集合。</summary>
    internal void ApplyUserBlockComposition(UserBlockComposition? composition)
    {
        Composition = composition ?? UserBlockComposition.Empty;
        RecomputeEffectiveDefinitions();
    }

    /// <summary>由文档加载通道写入复合阶段声明；注册表在首次渲染时按此集合惰性重建。</summary>
    internal void ApplyGraphStages(IReadOnlyList<CompositeStageDefinition>? stages)
    {
        GraphStages = stages ?? [];
    }

    /// <summary>
    /// 展开只在装载边界发生一次：之后的渲染/解析/失效判定都读
    /// <see cref="EffectiveUserBlockDefinitions"/>，不会每条路径各展开一遍。
    /// </summary>
    private void RecomputeEffectiveDefinitions()
    {
        _effectiveUserBlockDefinitions = UserBlockCompositionRules.ExpandAll(UserBlockDefinitions, Composition);
    }

    /// <summary>
    /// 广播“定义与声明默认值都已落地”。调用方必须在本轮定义变更的全部
    /// 内存改写（重解析、默认值注入）完成之后调用，订阅方才能读到完整状态。
    /// </summary>
    internal void NotifyUserBlockDefinitionsChanged() =>
        UserBlockDefinitionsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 挂上/摘掉某个自定义元素的结构图。图本身存在 <c>.md.graph.xml</c>，
    /// 这里只改内存定义，让下一次渲染按图解析结构。
    /// </summary>
    public bool ApplyElementStructureGraph(string definitionId, NodeGraph? graph)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);

        var index = -1;
        for (var position = 0; position < UserBlockDefinitions.Count; position++)
        {
            if (string.Equals(UserBlockDefinitions[position].Id, definitionId, StringComparison.Ordinal))
            {
                index = position;
                break;
            }
        }

        if (index < 0)
        {
            return false;
        }

        var next = new List<UserBlockDefinition>(UserBlockDefinitions);
        next[index] = next[index] with { StructureGraph = graph };
        UserBlockDefinitions = next;
        RecomputeEffectiveDefinitions();
        NotifyUserBlockDefinitionsChanged();
        return true;
    }

    public long SourceRevision => _sourceRevision;
    public long SavedSourceRevision => _savedSourceRevision;
    public long StyleRevision => _styleRevision;
    public long SavedStyleRevision => _savedStyleRevision;

    public long Revision => Math.Max(_sourceRevision, _styleRevision);
    public long SavedRevision => Math.Max(_savedSourceRevision, _savedStyleRevision);

    public bool IsSourceModified => _sourceRevision != _savedSourceRevision ||
                                   !string.Equals(_sourceMarkdown, _savedSourceMarkdown, StringComparison.Ordinal);

    public bool IsStyleModified => _styleRevision != _savedStyleRevision;

    public bool IsModified => IsSourceModified || IsStyleModified;

    public MarkdownDocument()
    {
        _root = CreateDefaultRoot();
        _savedStyleFingerprint = CreateStyleFingerprint();
    }

    public MarkdownNode? FindNode(string id) =>
        string.IsNullOrWhiteSpace(id) ? null : _root.FindById(id);

    /// <summary>
    /// 按节点 custom-kind 与属性键查定义声明的默认值；“恢复默认”操作据此
    /// 把实例值替换成 <see cref="AttributeValueSource.DefinitionDefault"/>。
    /// 节点不是自定义块、定义缺失或未声明默认值时返回 null。
    /// </summary>
    public string? GetUserBlockAttributeDefault(MarkdownNode node, string key)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (node.Type != NodeType.Custom
            || !node.Attributes.TryGetValue("custom-kind", out var kind))
        {
            return null;
        }

        foreach (var definition in EffectiveUserBlockDefinitions)
        {
            if (!string.Equals(definition.Id, kind, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var declaration in definition.Attributes)
            {
                if (string.Equals(declaration.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return declaration.DefaultValue;
                }
            }

            return null;
        }

        return null;
    }

    public void Validate()
    {
        var error = GetValidationIssues()
            .FirstOrDefault(issue => issue.Severity == DocumentValidationSeverity.Error);

        if (error is not null)
            throw new DocumentValidationException($"[{error.Code}] {error.Message}");
    }

    public IReadOnlyList<DocumentValidationIssue> GetValidationIssues()
    {
        var issues = new List<DocumentValidationIssue>();

        if (Root is null)
        {
            issues.Add(new DocumentValidationIssue(DocumentValidationSeverity.Error, "NULL_ROOT", "Document root cannot be null."));
            return issues;
        }

        if (Root.Type != NodeType.Document)
            issues.Add(new DocumentValidationIssue(DocumentValidationSeverity.Error, "INVALID_ROOT_TYPE", "Document root must have Document node type.", Root.Id));

        if (!Root.IsSynthetic)
            issues.Add(new DocumentValidationIssue(DocumentValidationSeverity.Error, "INVALID_ROOT_SYNTHETIC_STATE", "Document root must be synthetic.", Root.Id));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        ValidateNode(Root, expectedParent: null, ids, issues);

        return issues;
    }

    public bool UpdateSourceMarkdown(string? source)
    {
        source ??= string.Empty;
        if (string.Equals(_sourceMarkdown, source, StringComparison.Ordinal))
            return false;

        _sourceMarkdown = source;
        MarkSourceChanged();
        return true;
    }

    internal void ReplaceParsedRoot(MarkdownNode root, string? source, bool markChanged)
    {
        ArgumentNullException.ThrowIfNull(root);
        source ??= string.Empty;

        // 共享节点 Parent 切换与换根必须在同一临界区：StyleResolver.Resolve
        // 持同一把锁沿 Parent 上行。Monitor 可重入，后台提交路径已持锁。
        lock (this)
        {
            var pendingParentLinks = root.PendingParentLinks;
            if (pendingParentLinks is not null)
            {
                foreach (var (child, newParent) in pendingParentLinks)
                {
                    child.Parent = newParent;
                }

                root.PendingParentLinks = null;
            }

            var sourceChanged = !string.Equals(_sourceMarkdown, source, StringComparison.Ordinal);

            _root = root;
            _sourceMarkdown = source;
            LastParsedSource = source;

            NormalizeEditorState();

            if (markChanged && sourceChanged)
                MarkSourceChanged();
        }
    }

    internal void RestoreLoadedState(
        string? source,
        MarkdownNode root,
        StyleSheet styleSheet,
        DocumentMetadata metadata,
        EditorState editorState,
        string? id,
        string? filePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(styleSheet);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(editorState);

        _sourceMarkdown = source ?? string.Empty;
        _root = root;
        LastParsedSource = _sourceMarkdown;
        StyleSheet = styleSheet;
        Metadata = metadata;
        EditorState = editorState;

        if (!string.IsNullOrWhiteSpace(id)) Id = id;
        if (!string.IsNullOrWhiteSpace(filePath)) FilePath = filePath;

        MarkSaved();
    }

    /// <summary>
    /// 把类型定义里显式声明的默认值注入节点生效属性（来源 <see cref="AttributeValueSource.DefinitionDefault"/>），
    /// 让只读 <see cref="MarkdownNode.Attributes"/> 的渲染层也能读到定义默认。
    ///
    /// 优先级（低 → 高）：渲染器内置默认 &lt; 样式声明默认 &lt; 元素定义默认 &lt; 面板实例值 &lt; 源码值。
    /// 「样式声明默认」= <see cref="StyleDefinition.Attributes"/> 中 <c>DefaultValue</c> 非空的声明；
    /// 「元素定义默认」= <see cref="UserBlockDefinition.Attributes"/>（自定义元素的 .md.elements.xml）。
    /// 两处同键且值不同时元素定义默认胜出（实测见
    /// <c>ElementAttributeDefaultProvenanceProbeTests.StyleDeclarationDefault_DoesNotOverrideElementDefault</c>）：
    /// 解析期先把元素定义默认按 DefinitionDefault 写进生效属性，本方法不覆盖既有生效值，
    /// 样式声明默认只在键缺席时补位。
    ///
    /// 只在声明了 DefaultValue 时注入；未声明保持缺席，继续由渲染器内置默认兜底，
    /// 因此没有类型声明的文档渲染结果不变（渲染基线依赖这一点，清单见测试侧 manifest）。
    /// 声明被删除或默认值被改动时，同步清理/更新此前由声明注入的 DefinitionDefault 值；
    /// 已有源码值或实例值的键一律不动（它们的优先级更高）。
    /// </summary>
    public void ApplyDeclaredAttributeDefaults()
    {
        lock (this)
        {
            var index = BuildDefinitionIndex();
            foreach (var node in EnumerateSelfAndDescendants(_root))
            {
                if (node.Type == NodeType.Document)
                {
                    continue;
                }

                var elementDefaults = ResolveElementDefaults(node);
                var styleDefaults = index.ResolveDeclarationDefaults(node);

                // 先处理上一轮注入值：不再声明的清掉，默认值改过的更新到最新。
                foreach (var key in node.Attributes.Keys.ToList())
                {
                    if (node.GetAttributeSource(key) != AttributeValueSource.DefinitionDefault)
                    {
                        continue;
                    }

                    if (elementDefaults.TryGetValue(key, out var elementValue))
                    {
                        if (!string.Equals(node.Attributes[key], elementValue, StringComparison.Ordinal))
                        {
                            node.RestoreAttribute(key, elementValue, AttributeValueSource.DefinitionDefault);
                        }

                        continue;
                    }

                    if (styleDefaults.TryGetValue(key, out var styleValue))
                    {
                        if (!string.Equals(node.Attributes[key], styleValue, StringComparison.Ordinal))
                        {
                            node.RestoreAttribute(key, styleValue, AttributeValueSource.DefinitionDefault);
                        }

                        continue;
                    }

                    node.RemoveAttribute(key);
                }

                // 高处优先：元素定义默认先占位，样式声明默认只补缺席的键。
                foreach (var pair in elementDefaults)
                {
                    node.SetDefinitionDefaultAttribute(pair.Key, pair.Value);
                }

                foreach (var pair in styleDefaults)
                {
                    node.SetDefinitionDefaultAttribute(pair.Key, pair.Value);
                }
            }
        }
    }

    /// <summary>
    /// 取类型样式声明里显式给出的默认值（kind 限定定义优先于类型定义，与渲染器解析一致）。
    /// 元素定义默认（自定义块）不在这里，调用方按 <see cref="GetUserBlockAttributeDefault"/> 先查。
    /// </summary>
    public string? GetDeclaredAttributeDefault(MarkdownNode node, string key)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return BuildDefinitionIndex().ResolveDeclarationDefaults(node).TryGetValue(key, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// 重开文档包时按「值 = 定义默认 → 定义默认，值 ≠ 定义默认 → 实例值」反推来源。
    /// 文件格式不为来源单独落盘（保持版本兼容），所以只能近似：
    /// 用户手动设过、且值恰好等于定义默认的键会被判成定义默认——这是可接受的近似，
    /// 画布徽标 tooltip 已注明“值等于定义默认，无法区分是否手动设过”。
    /// 判成定义默认时同时丢弃实例层回退值，避免下一次重解析又被迁移成面板覆写。
    /// </summary>
    public void InferAttributeSourcesFromDefinitions()
    {
        lock (this)
        {
            var index = BuildDefinitionIndex();
            foreach (var node in EnumerateSelfAndDescendants(_root))
            {
                if (node.Type == NodeType.Document)
                {
                    continue;
                }

                var elementDefaults = ResolveElementDefaults(node);
                var styleDefaults = index.ResolveDeclarationDefaults(node);
                foreach (var key in node.Attributes.Keys.ToList())
                {
                    if (node.GetAttributeSource(key) != AttributeValueSource.Instance)
                    {
                        continue;
                    }

                    if (!elementDefaults.TryGetValue(key, out var declared)
                        && !styleDefaults.TryGetValue(key, out declared))
                    {
                        continue;
                    }

                    if (!string.Equals(node.Attributes[key], declared, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    node.AttributeSources[key] = AttributeValueSource.DefinitionDefault;
                    node.RemoveInstanceAttribute(key);
                }
            }
        }
    }

    private DefinitionIndex BuildDefinitionIndex()
    {
        var index = new DefinitionIndex();
        foreach (var definition in StyleSheet.Styles)
        {
            if (definition.AppliesTo.HasValue && !IsDefaultStyleId(definition.Id))
            {
                index.Add(definition);
            }
        }

        return index;
    }

    private static bool IsDefaultStyleId(string id) =>
        string.Equals(id, "default", StringComparison.Ordinal);

    /// <summary>自定义元素的定义默认；自定义块之外没有元素定义默认。</summary>
    private Dictionary<string, string> ResolveElementDefaults(MarkdownNode node)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node.Type != NodeType.Custom)
        {
            return result;
        }

        var kind = ElementKind.Resolve(node);
        if (kind is null)
        {
            return result;
        }

        foreach (var definition in EffectiveUserBlockDefinitions)
        {
            if (!string.Equals(definition.Id, kind, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var declaration in definition.Attributes)
            {
                if (declaration.DefaultValue is not null)
                {
                    result.TryAdd(declaration.Key, declaration.DefaultValue);
                }
            }

            break;
        }

        return result;
    }

    /// <summary>类型样式声明的查找表：kind 限定优先，与渲染器 TypeDefinitionIndex 同规则。</summary>
    private sealed class DefinitionIndex
    {
        private readonly Dictionary<NodeType, StyleDefinition> _byType = [];
        private readonly Dictionary<(NodeType Type, string Kind), StyleDefinition> _byKind =
            new(ElementKind.KeyComparer);

        public void Add(StyleDefinition definition)
        {
            var type = definition.AppliesTo!.Value;
            if (string.IsNullOrWhiteSpace(definition.AppliesToKind))
            {
                _byType.TryAdd(type, definition);
            }
            else
            {
                _byKind.TryAdd((type, definition.AppliesToKind.Trim()), definition);
            }
        }

        public Dictionary<string, string> ResolveDeclarationDefaults(MarkdownNode node)
        {
            var kind = ElementKind.Resolve(node);
            StyleDefinition? definition = null;
            if (kind is not null)
            {
                _byKind.TryGetValue((node.Type, kind), out definition);
            }

            if (definition is null)
            {
                _byType.TryGetValue(node.Type, out definition);
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (definition is null)
            {
                return result;
            }

            foreach (var declaration in definition.Attributes)
            {
                if (!string.IsNullOrWhiteSpace(declaration.Key) && declaration.DefaultValue is not null)
                {
                    result.TryAdd(declaration.Key, declaration.DefaultValue);
                }
            }

            return result;
        }
    }

    public void MarkSourceChanged()
    {
        Interlocked.Increment(ref _sourceRevision);
        Metadata.ModifiedUtc = DateTime.UtcNow;
    }

    public void MarkStyleChanged()
    {
        Interlocked.Increment(ref _styleRevision);
        Metadata.ModifiedUtc = DateTime.UtcNow;
    }

    public void MarkChanged() => MarkSourceChanged();

    public void MarkSaved()
    {
        _savedSourceRevision = _sourceRevision;
        _savedStyleRevision = _styleRevision;
        _savedSourceMarkdown = _sourceMarkdown;
        _savedStyleFingerprint = CreateStyleFingerprint();
    }

    public void NormalizeEditorState()
    {
        if (!string.IsNullOrWhiteSpace(EditorState.SelectedNodeId) || EditorState.ExpandedNodeIds.Count > 0)
        {
            var nodeIds = new HashSet<string>(StringComparer.Ordinal);
            var pending = new List<MarkdownNode> { _root };
            while (pending.Count > 0)
            {
                int lastIndex = pending.Count - 1;
                var current = pending[lastIndex];
                pending.RemoveAt(lastIndex);

                nodeIds.Add(current.Id);
                for (int i = 0; i < current.Children.Count; i++)
                    pending.Add(current.Children[i]);
            }

            if (!string.IsNullOrWhiteSpace(EditorState.SelectedNodeId) &&
                !nodeIds.Contains(EditorState.SelectedNodeId))
            {
                EditorState.SelectedNodeId = null;
            }

            EditorState.ExpandedNodeIds.RemoveWhere(nodeId => !nodeIds.Contains(nodeId));
        }

        EditorState.CaretOffset = Math.Clamp(EditorState.CaretOffset, 0, SourceMarkdown.Length);
        EditorState.SelectionLength = Math.Clamp(EditorState.SelectionLength, 0, SourceMarkdown.Length - EditorState.CaretOffset);
    }

    private static void ValidateNode(MarkdownNode node, MarkdownNode? expectedParent, ISet<string> ids, ICollection<DocumentValidationIssue> issues)
    {
        if (!ReferenceEquals(node.Parent, expectedParent))
            issues.Add(new DocumentValidationIssue(DocumentValidationSeverity.Error, "INVALID_PARENT", "Node has an invalid parent reference.", node.Id));

        if (string.IsNullOrWhiteSpace(node.Id))
            issues.Add(new DocumentValidationIssue(DocumentValidationSeverity.Error, "EMPTY_NODE_ID", "Document node Id cannot be empty."));
        else if (!ids.Add(node.Id))
            issues.Add(new DocumentValidationIssue(DocumentValidationSeverity.Error, "DUPLICATE_NODE_ID", $"Duplicate node Id '{node.Id}'.", node.Id));

        foreach (var child in node.Children)
            ValidateNode(child, expectedParent: node, ids, issues);
    }

    public string CreateStyleFingerprint()
    {
        var builder = new StringBuilder(1024);

        builder.Append("STYLESHEET:");
        foreach (var def in StyleSheet.Styles.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            builder.Append(
                $"[ID={def.Id};P={def.ParentStyleId};A={def.AppliesTo};K={def.AppliesToKind};B={def.Behavior.FrameStrategy};V={def.Behavior.TemplateVariant};S=");
            AppendNodeStyleFast(builder, def.Style);

            // 类型声明也是样式数据的一部分，改了声明必须让保存态指纹变化。
            if (def.Attributes.Count > 0)
            {
                builder.Append(";ATTRS=");
                foreach (var attribute in def.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal))
                {
                    builder.Append(
                        $"{attribute.Key}:{attribute.ValueKind}:{attribute.IsEnabled}:{attribute.Target}:{attribute.DefaultValue}:{string.Join(',', attribute.Options)}");
                    if (attribute.Binding is { IsEmpty: false } binding)
                    {
                        builder.Append(
                            $":B={binding.Target}:{binding.Name}:{binding.Part}:{binding.ValueMap}");
                    }

                    // 呈现设置只影响编辑侧，但同样属于类型声明数据，改了也要触发保存态更新。
                    var presentation = ElementAttributePresentationEvaluator.Describe(attribute.Presentation);
                    if (presentation.Length > 0)
                    {
                        builder.Append(":P=").Append(presentation);
                    }

                    builder.Append(';');
                }
            }

            builder.Append(']');
        }
        builder.Append('|');

        builder.Append("NODES:");
        foreach (var candidate in EnumerateSelfAndDescendants(_root))
        {
            if (!string.IsNullOrWhiteSpace(candidate.StyleId) || !IsDefaultStyle(candidate.LocalStyle) || candidate.Attributes.Count > 0)
            {
                builder.Append($"[NID={candidate.Id};SID={candidate.StyleId};S=");
                AppendNodeStyleFast(builder, candidate.LocalStyle);
                builder.Append(";ATTRS=");
                foreach (var attr in candidate.Attributes.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    builder.Append($"{attr.Key}={attr.Value};");
                }
                builder.Append(']');
            }
        }
        builder.Append('|');

        builder.Append($"META:{Metadata.Title}|{Metadata.Author}|{Metadata.Language}|{Metadata.Encoding}|");
        foreach (var property in Metadata.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
            builder.Append($"{property.Key}={property.Value};");
        builder.Append('|');

        builder.Append($"STATE:{EditorState.SelectedNodeId}|{EditorState.CaretOffset}|{EditorState.SelectionLength}|");
        foreach (var nodeId in EditorState.ExpandedNodeIds.OrderBy(v => v, StringComparer.Ordinal))
            builder.Append($"{nodeId};");

        return ComputeSha256(builder.ToString());
    }

    private static void AppendNodeStyleFast(StringBuilder sb, NodeStyle? s)
    {
        if (s is null) return;
        sb.Append($"{s.FontFamily};{s.FontSize};{s.ForegroundColor};{s.BackgroundColor};{s.Bold};{s.Italic};{s.LineHeight};{s.TextAlign};{s.BorderColor};{s.BorderWidth};{s.BorderRadius};{s.CustomCss};");
        if (s.Margin is not null) sb.Append($"M:{s.Margin.Left},{s.Margin.Top},{s.Margin.Right},{s.Margin.Bottom};");
        if (s.Padding is not null) sb.Append($"P:{s.Padding.Left},{s.Padding.Top},{s.Padding.Right},{s.Padding.Bottom};");
    }

    private static bool IsDefaultStyle(NodeStyle? style) =>
        style is null || (style.FontSize is null && style.FontFamily is null && style.ForegroundColor is null &&
        style.BackgroundColor is null && style.Bold is null && style.Italic is null &&
        style.LineHeight is null && style.TextAlign is null && style.Margin is null &&
        style.Padding is null && style.BorderColor is null && (style.BorderWidth is null or 0) &&
        style.BorderRadius is null &&
        string.IsNullOrWhiteSpace(style.CustomCss));

    private static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static MarkdownNode CreateDefaultRoot() => new()
    {
        Type = NodeType.Document,
        Category = NodeCategory.Synthetic,
        IsSynthetic = true,
        Text = "Document"
    };

    private static IEnumerable<MarkdownNode> EnumerateSelfAndDescendants(MarkdownNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in EnumerateSelfAndDescendants(child))
                yield return descendant;
    }
}
