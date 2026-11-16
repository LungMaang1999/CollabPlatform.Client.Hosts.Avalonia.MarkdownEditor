using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 把 <see cref="UserBlockDefinition"/> 里声明的约束真正落到解析结果上：
/// 属性取值校验/默认值注入/必填检查，以及内容模型（允许的块与行内）校验。
///
/// 策略（与报告中的策略表一致）：
/// - 未知属性：Warning + 保留在节点属性里，用户信息不丢；
/// - 非法取值：Warning；有默认值则回退默认值，否则保留原值；
/// - 必填缺失：「有效值」不存在才报（默认值也算有效值）；
/// - 内容违规：照常构建子树，只在节点上打 <c>content-violation</c> 标记并报诊断，
///   绝不丢弃用户内容。
/// </summary>
internal static class UserBlockConstraintValidator
{
    /// <summary>
    /// 根据声明处理实例属性：写入默认值、回退非法值、报告未知键与缺失必填。
    /// <paramref name="sourceAttributes"/> 只包含源码里显式给出的、非派生键的属性。
    /// </summary>
    public static void ApplyAttributes(
        UserBlockDefinition definition,
        IReadOnlyList<KeyValuePair<string, string>> sourceAttributes,
        MarkdownNode node,
        ICollection<DiagnosticMessage> diagnostics,
        SourceRange headerRange)
    {
        var declarations = new Dictionary<string, UserBlockAttributeDeclaration>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in definition.Attributes)
        {
            declarations.TryAdd(declaration.Key, declaration);
        }

        var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in sourceAttributes)
        {
            provided.Add(pair.Key);
            if (!declarations.TryGetValue(pair.Key, out var declaration))
            {
                // 定义没声明：保留值，只提示，不阻断。
                diagnostics.Add(Warning(
                    "USER_BLOCK_ATTRIBUTE_UNDECLARED",
                    $"自定义元素 '{definition.Name}' 第 {headerRange.StartLine} 行的属性 '{pair.Key}' 未在定义中声明；"
                    + "该值已保留在节点属性中，不参与取值校验。",
                    headerRange));
                continue;
            }

            if (UserBlockAttributeRules.IsValidValue(declaration.Kind, pair.Value, declaration.AllowedValues))
            {
                continue;
            }

            var constraint = UserBlockAttributeRules.DescribeConstraint(declaration.Kind, declaration.AllowedValues);
            if (declaration.DefaultValue is not null)
            {
                // 源码键存在但取值非法：生效值回退默认值，来源仍记 Source
                // （源码声明了这个键，实例层不得越过它接管）。
                node.SetSourceAttribute(pair.Key, declaration.DefaultValue);
                diagnostics.Add(Warning(
                    "USER_BLOCK_ATTRIBUTE_VALUE_INVALID",
                    $"自定义元素 '{definition.Name}' 第 {headerRange.StartLine} 行的属性 '{pair.Key}' 取值 "
                    + $"'{pair.Value}' 非法（{constraint}）；已回退默认值 '{declaration.DefaultValue}'。",
                    headerRange));
            }
            else
            {
                diagnostics.Add(Warning(
                    "USER_BLOCK_ATTRIBUTE_VALUE_INVALID",
                    $"自定义元素 '{definition.Name}' 第 {headerRange.StartLine} 行的属性 '{pair.Key}' 取值 "
                    + $"'{pair.Value}' 非法（{constraint}）；未声明默认值，已保留原值。",
                    headerRange));
            }
        }

        foreach (var declaration in definition.Attributes)
        {
            if (provided.Contains(declaration.Key))
            {
                continue;
            }

            if (declaration.DefaultValue is not null)
            {
                // 默认值以 DefinitionDefault 来源进入节点属性：渲染层直接可读，
                // 而身份匹配/实例层可以按优先级覆盖它。
                node.SetDefinitionDefaultAttribute(declaration.Key, declaration.DefaultValue);
            }
            else if (declaration.Required)
            {
                diagnostics.Add(Warning(
                    "USER_BLOCK_ATTRIBUTE_REQUIRED_MISSING",
                    $"自定义元素 '{definition.Name}' 第 {headerRange.StartLine} 行缺少必填属性 '{declaration.Key}'。",
                    headerRange));
            }
        }
    }

    /// <summary>
    /// 校验内容模型。返回 true 表示存在违规（调用方据此打标记）。
    /// 块级只校验直接子块；行内校验允许块内的全部行内后代，
    /// 已判定违规的块不再展开其行内，避免同一条内容被重复报两次。
    /// </summary>
    public static bool ValidateContent(
        UserBlockDefinition definition,
        MarkdownNode node,
        ICollection<DiagnosticMessage> diagnostics)
    {
        if (definition.Content.AllowsAnyBlock && definition.Content.AllowsAnyInline)
        {
            return false;
        }

        var violated = false;

        IEnumerable<MarkdownNode> inlineScope = node.Children;
        if (!definition.Content.AllowsAnyBlock)
        {
            var allowedBlocks = new HashSet<NodeType>(definition.Content.AllowedBlocks);
            var allowedText = string.Join("、", definition.Content.AllowedBlocks);
            var allowedChildren = new List<MarkdownNode>(node.Children.Count);

            foreach (var child in node.Children)
            {
                if (allowedBlocks.Contains(child.Type))
                {
                    allowedChildren.Add(child);
                    continue;
                }

                violated = true;
                diagnostics.Add(Warning(
                    "USER_BLOCK_CONTENT_BLOCK_NOT_ALLOWED",
                    $"自定义元素 '{definition.Name}' 第 {child.Range.StartLine} 行出现不允许的块级元素 "
                    + $"{child.Type}（定义允许：{allowedText}）；内容已照常保留，未做删改。",
                    child.Range));
            }

            inlineScope = allowedChildren;
        }

        if (!definition.Content.AllowsAnyInline)
        {
            var allowedInlines = new HashSet<NodeType>(definition.Content.AllowedInlines);
            var allowedText = string.Join("、", definition.Content.AllowedInlines);

            // 同一 (类型, 行) 只报一次：一段文字里多个链接不该刷出多条。
            var reported = new HashSet<(NodeType Type, int Line)>();
            foreach (var block in inlineScope)
            {
                if (allowedInlines.Contains(block.Type))
                {
                    continue;
                }

                foreach (var descendant in block.Descendants())
                {
                    if (descendant.Category != NodeCategory.Inline
                        || allowedInlines.Contains(descendant.Type)
                        || !reported.Add((descendant.Type, descendant.Range.StartLine)))
                    {
                        continue;
                    }

                    violated = true;
                    diagnostics.Add(Warning(
                        "USER_BLOCK_CONTENT_INLINE_NOT_ALLOWED",
                        $"自定义元素 '{definition.Name}' 第 {descendant.Range.StartLine} 行出现不允许的行内元素 "
                        + $"{descendant.Type}（定义允许：{allowedText}）；内容已照常保留，未做删改。",
                        descendant.Range));
                }
            }
        }

        if (violated)
        {
            node.SetDerivedAttribute(MarkdownAttributes.ContentViolation, "true");
        }

        return violated;
    }

    private static DiagnosticMessage Warning(string code, string message, SourceRange range) => new()
    {
        Severity = DiagnosticSeverity.Warning,
        Code = code,
        Message = message,
        Range = range
    };
}
