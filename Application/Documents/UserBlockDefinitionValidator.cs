using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Documents;

/// <summary>
/// 元素定义编辑期的纯校验。诊断码与 <c>UserBlockElementsSerializer</c> 共用同一套
/// <c>USER_ELEMENTS_*</c>，只是编辑路径把“可降级”的问题提升为阻断提交的 Error——
/// 用户明确提交的定义没理由带着半合法状态落盘。
/// </summary>
public static class UserBlockDefinitionValidator
{
    /// <summary>
    /// 校验一条候选定义。<paramref name="replacingId"/> 非空表示“修改”场景：
    /// 该 Id 的旧定义不参与重复判定（允许改名）。
    /// <paramref name="composition"/> 是当前生效的组合单元；缺引用/环/部件名碰撞在这里
    /// 是阻断保存的 Error——作者正坐在编辑界面前，静默丢弃会让他以为存成功了。
    /// 装载期走同一个校验函数（<c>UserBlockElementsSerializer</c>），但策略是丢定义 + 诊断。
    /// </summary>
    public static IReadOnlyList<UserBlockDefinitionEditIssue> Validate(
        UserBlockDefinition definition,
        IEnumerable<UserBlockDefinition>? existing = null,
        string? replacingId = null,
        UserBlockComposition? composition = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var issues = new List<UserBlockDefinitionEditIssue>();

        if (!UserBlockRegistry.IsValidIdentifier(definition.Id))
        {
            issues.Add(Issue(
                "USER_ELEMENTS_ID_INVALID",
                $"用户元素定义的 Id '{definition.Id}' 非法：需字母开头，仅含字母、数字、下划线或连字符，最长 64。",
                definition.Id,
                "id"));
            // Id 非法时前缀无法拼出有意义的定位信息，后续检查仍按原样给出。
        }
        else
        {
            foreach (var other in existing ?? [])
            {
                if (replacingId is not null
                    && string.Equals(other.Id, replacingId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(other.Id, definition.Id, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(Issue(
                        "USER_ELEMENTS_ID_DUPLICATE",
                        $"用户元素定义 Id '{definition.Id}' 与已有定义重复，请换一个 Id。",
                        definition.Id,
                        "id"));
                    break;
                }
            }
        }

        if (!UserBlockRegistry.IsValidIdentifier(definition.Name))
        {
            issues.Add(Issue(
                "USER_ELEMENTS_NAME_INVALID",
                $"用户元素 '{definition.Id}' 的名称 '{definition.Name}' 非法：需字母开头，仅含字母、数字、下划线或连字符，最长 64。",
                definition.Id,
                "name"));
        }
        else if (definition.EffectiveFenceChars.Any(candidate =>
                     UserBlockRegistry.FamilyOf(candidate) is { } family
                     && FenceKeywordRegistry.BuiltIn.IsReservedKeyword(family, definition.Name)))
        {
            // 内置关键字是保留字：定义与内置撞车必须拒绝，不能静默顶掉内置围栏。
            issues.Add(Issue(
                "USER_ELEMENTS_NAME_RESERVED",
                $"用户元素 '{definition.Id}' 的名称 '{definition.Name}' 是内置围栏关键字，不能作为元素名称。",
                definition.Id,
                "name"));
        }

        if (!UserBlockRegistry.TryGetFenceCharIssue(definition.FenceChar, out var fenceCode, out var fenceDetail))
        {
            issues.Add(Issue(
                fenceCode,
                $"用户元素 '{definition.Id}' 的围栏字符不可用；{fenceDetail}。",
                definition.Id,
                "fence-char"));
        }

        if (definition.FenceChars.Count > 0)
        {
            if (definition.FenceChars[0] != definition.FenceChar)
            {
                issues.Add(Issue(
                    "USER_ELEMENTS_FENCE_CHARS_CONFLICT",
                    $"用户元素 '{definition.Id}' 的多围栏声明首项 '{definition.FenceChars[0]}' "
                    + $"与 fence-char '{definition.FenceChar}' 不一致。",
                    definition.Id,
                    "fence-chars"));
            }

            var declared = new HashSet<char>();
            foreach (var fenceChar in definition.FenceChars)
            {
                if (!UserBlockRegistry.TryGetFenceCharIssue(fenceChar, out var multiFenceCode, out var multiFenceDetail))
                {
                    issues.Add(Issue(
                        multiFenceCode,
                        $"用户元素 '{definition.Id}' 的多围栏声明不可用；{multiFenceDetail}。",
                        definition.Id,
                        "fence-chars"));
                }
                else if (!declared.Add(fenceChar))
                {
                    issues.Add(Issue(
                        "USER_ELEMENTS_FENCE_CHARS_DUPLICATE",
                        $"用户元素 '{definition.Id}' 的多围栏声明含重复字符 '{fenceChar}'。",
                        definition.Id,
                        "fence-chars"));
                }
            }
        }

        if (definition.FenceLength is < UserBlockRegistry.MinFenceLength or > UserBlockRegistry.MaxFenceLength)
        {
            issues.Add(Issue(
                "USER_ELEMENTS_FENCE_LENGTH_OUT_OF_RANGE",
                $"用户元素 '{definition.Id}' 的围栏长度 {definition.FenceLength} 越界：允许 "
                + $"{UserBlockRegistry.MinFenceLength}~{UserBlockRegistry.MaxFenceLength}。",
                definition.Id,
                "fence-length"));
        }

        if (definition.Priority is < UserBlockRegistry.MinPriority or > UserBlockRegistry.MaxPriority)
        {
            issues.Add(Issue(
                "USER_ELEMENTS_PRIORITY_OUT_OF_RANGE",
                $"用户元素 '{definition.Id}' 的优先级 {definition.Priority} 越界：允许 "
                + $"{UserBlockRegistry.MinPriority}~{UserBlockRegistry.MaxPriority}。",
                definition.Id,
                "priority"));
        }

        ValidateAliases(definition, existing, replacingId, issues);
        ValidateAttributes(definition, issues);
        ValidateContent(definition, issues);

        // 父链的解析域：现有定义 + 候选定义（候选覆盖同 Id 的旧定义）。
        var scope = BuildScope(definition, existing, replacingId);

        // 组合/继承定义的字段校验必须在展开结果上做：属性组把属性合并进来、
        // 片段把部件拼进来、继承把父定义铺平，未展开的结构里这些还都看不到。
        var effective = UserBlockCompositionRules.UsesComposition(definition)
                        || UserBlockCompositionRules.UsesInheritance(definition)
            ? UserBlockCompositionRules.Expand(definition, composition, scope)
            : definition;
        ValidateStructure(effective, issues);
        ValidateComposition(definition, composition ?? UserBlockComposition.Empty, scope, issues);

        return issues;
    }

    /// <summary>
    /// 解析 <c>extends</c> 父链用的定义集合：现有定义（去掉被替换的旧定义）在前，候选定义在后。
    /// 候选在最后加入，确保“改名 + 改父”这种编辑也不会读到旧版本。
    /// </summary>
    private static IReadOnlyList<UserBlockDefinition> BuildScope(
        UserBlockDefinition definition,
        IEnumerable<UserBlockDefinition>? existing,
        string? replacingId)
    {
        var scope = new List<UserBlockDefinition>();
        foreach (var other in existing ?? [])
        {
            if (replacingId is not null
                && string.Equals(other.Id, replacingId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(other.Id, definition.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            scope.Add(other);
        }

        scope.Add(definition);
        return scope;
    }

    /// <summary>
    /// 定义间组合的保存期校验：缺引用、继承成环/父定义缺失、环、部件名碰撞、属性键重复一律阻断保存。
    /// 与装载期调用同一个 <see cref="UserBlockCompositionRules.Validate"/>，只是策略不同。
    /// </summary>
    private static void ValidateComposition(
        UserBlockDefinition definition,
        UserBlockComposition composition,
        IReadOnlyList<UserBlockDefinition> scope,
        List<UserBlockDefinitionEditIssue> issues)
    {
        foreach (var issue in UserBlockCompositionRules.Validate(definition, composition, scope))
        {
            var field = issue.Code switch
            {
                UserBlockCompositionRules.PartCollisionCode => "structure",
                UserBlockCompositionRules.CycleCode => "extends",
                UserBlockCompositionRules.MissingRefCode =>
                    UserBlockCompositionRules.UsesInheritance(definition) ? "extends" : "composition",
                "USER_ELEMENTS_ATTRIBUTE_DUPLICATE" => "attributes",
                _ => "composition"
            };
            issues.Add(Issue(issue.Code, issue.Message, definition.Id, field));
        }
    }

    /// <summary>
    /// 结构里的 <c>{field}</c> 标签插值与 <c>field==value</c> 条件（部件/条件类名）都必须命中
    /// 已声明的枚举字段，且每个取值都在枚举内；任一不满足即阻断保存（不静默丢弃该部件）。
    /// </summary>
    private static void ValidateStructure(
        UserBlockDefinition definition,
        List<UserBlockDefinitionEditIssue> issues)
    {
        foreach (var issue in UserBlockStructureRules.ValidateTagFields(
                     definition.Structure,
                     definition.Attributes))
        {
            issues.Add(Issue(issue.Code, issue.Message, definition.Id, "structure"));
        }

        foreach (var issue in UserBlockStructureRules.ValidateWhenFields(
                     definition.Structure,
                     definition.Attributes))
        {
            issues.Add(Issue(issue.Code, issue.Message, definition.Id, "structure"));
        }

        foreach (var issue in UserBlockStructureRules.ValidateWhenSourceFields(definition.Structure))
        {
            issues.Add(Issue(issue.Code, issue.Message, definition.Id, "structure"));
        }

        foreach (var issue in UserBlockStructureRules.ValidateRepeatFields(definition.Structure, definition.StructureKey))
        {
            issues.Add(Issue(issue.Code, issue.Message, definition.Id, "structure"));
        }

        foreach (var issue in UserBlockStructureRules.ValidateGeneratorFields(definition.Structure))
        {
            issues.Add(Issue(issue.Code, issue.Message, definition.Id, "structure"));
        }

        foreach (var issue in UserBlockStructureRules.ValidateRefFields(definition.Structure))
        {
            issues.Add(Issue(issue.Code, issue.Message, definition.Id, "structure"));
        }

        foreach (var issue in UserBlockStructureRules.ValidatePolicyFields(definition.Structure))
        {
            issues.Add(Issue(issue.Code, issue.Message, definition.Id, "structure"));
        }

        // 受控 URL 绑定（a→href、img→src）：标签配对、纯洞模板、字段已声明，任一不满足阻断保存。
        foreach (var issue in UserBlockStructureRules.ValidateBindingFields(
                     definition.Structure,
                     definition.Attributes))
        {
            issues.Add(Issue(issue.Code, issue.Message, definition.Id, "structure"));
        }
    }

    /// <summary>
    /// 别名校验：数量/格式/自指/定义内重复/内置保留字/与其他定义关键字重复，
    /// 每条都给固定码并阻断保存。别名冲突不做“后者覆盖前者”的静默仲裁。
    /// </summary>
    private static void ValidateAliases(
        UserBlockDefinition definition,
        IEnumerable<UserBlockDefinition>? existing,
        string? replacingId,
        List<UserBlockDefinitionEditIssue> issues)
    {
        if (definition.Aliases.Count > UserBlockDefinition.MaxAliases)
        {
            issues.Add(Issue(
                "USER_ELEMENTS_KEYWORD_COUNT_EXCEEDED",
                $"用户元素 '{definition.Id}' 声明了 {definition.Aliases.Count} 个别名，"
                + $"超过上限 {UserBlockDefinition.MaxAliases}。",
                definition.Id,
                "keywords"));
            return;
        }

        var others = (existing ?? [])
            .Where(other => replacingId is null
                            || !string.Equals(other.Id, replacingId, StringComparison.OrdinalIgnoreCase))
            .Where(other => definition.EffectiveFenceChars.Any(other.UsesFenceChar))
            .ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in definition.Aliases)
        {
            if (!UserBlockRegistry.IsValidIdentifier(alias))
            {
                issues.Add(Issue(
                    "USER_ELEMENTS_KEYWORD_INVALID",
                    $"用户元素 '{definition.Id}' 的别名 '{alias}' 非法：需字母开头，仅含字母、数字、下划线或连字符，最长 64。",
                    definition.Id,
                    "keywords"));
                continue;
            }

            if (string.Equals(alias, definition.Name, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Issue(
                    "USER_ELEMENTS_KEYWORD_SELF_REFERENCE",
                    $"用户元素 '{definition.Id}' 的别名 '{alias}' 与本名相同，必须移除该别名。",
                    definition.Id,
                    "keywords"));
                continue;
            }

            if (!seen.Add(alias))
            {
                issues.Add(Issue(
                    "USER_ELEMENTS_KEYWORD_DUPLICATE",
                    $"用户元素 '{definition.Id}' 的别名 '{alias}' 定义内重复。",
                    definition.Id,
                    "keywords"));
                continue;
            }

            if (definition.EffectiveFenceChars.Any(candidate =>
                    UserBlockRegistry.FamilyOf(candidate) is { } family
                    && FenceKeywordRegistry.BuiltIn.IsReservedKeyword(family, alias)))
            {
                issues.Add(Issue(
                    "USER_ELEMENTS_KEYWORD_RESERVED",
                    $"用户元素 '{definition.Id}' 的别名 '{alias}' 是内置围栏关键字，不能作为别名。",
                    definition.Id,
                    "keywords"));
                continue;
            }

            var owner = others.FirstOrDefault(other => other.MatchesKeyword(alias));
            if (owner is not null)
            {
                issues.Add(Issue(
                    "USER_ELEMENTS_KEYWORD_DUPLICATE",
                    $"用户元素 '{definition.Id}' 的别名 '{alias}' 与已有定义 '{owner.Id}' 的关键字重复。",
                    definition.Id,
                    "keywords"));
            }
        }

        // 反向：本名撞上其他定义的别名，同样拒绝，避免别名悄悄占住别人的名字。
        var nameOwner = others.FirstOrDefault(other => other.Aliases.Any(alias =>
            string.Equals(alias, definition.Name, StringComparison.OrdinalIgnoreCase)));
        if (nameOwner is not null)
        {
            issues.Add(Issue(
                "USER_ELEMENTS_KEYWORD_DUPLICATE",
                $"用户元素 '{definition.Id}' 的名称 '{definition.Name}' 与已有定义 '{nameOwner.Id}' 的别名重复。",
                definition.Id,
                "keywords"));
        }
    }

    private static void ValidateAttributes(
        UserBlockDefinition definition,
        List<UserBlockDefinitionEditIssue> issues)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var attribute in definition.Attributes)
        {
            if (!UserBlockRegistry.IsValidIdentifier(attribute.Key))
            {
                issues.Add(Issue(
                    "USER_ELEMENTS_ATTRIBUTE_INVALID",
                    $"用户元素 '{definition.Id}' 的属性键 '{attribute.Key}' 非法：需字母开头，仅含字母、数字、下划线或连字符，最长 64。",
                    definition.Id,
                    "attributes"));
                continue;
            }

            if (!keys.Add(attribute.Key))
            {
                issues.Add(Issue(
                    "USER_ELEMENTS_ATTRIBUTE_DUPLICATE",
                    $"用户元素 '{definition.Id}' 的属性键 '{attribute.Key}' 重复，请合并为一条声明。",
                    definition.Id,
                    "attributes"));
                continue;
            }

            var allowedValues = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in attribute.AllowedValues)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    issues.Add(Issue(
                        "USER_ELEMENTS_ATTRIBUTE_INVALID",
                        $"用户元素 '{definition.Id}' 的属性 '{attribute.Key}' 含空允许值，请删除或填写。",
                        definition.Id,
                        "attributes"));
                    continue;
                }

                if (!allowedValues.Add(value.Trim()))
                {
                    issues.Add(Issue(
                        "USER_ELEMENTS_ATTRIBUTE_DUPLICATE",
                        $"用户元素 '{definition.Id}' 的属性 '{attribute.Key}' 的允许值 '{value}' 重复。",
                        definition.Id,
                        "attributes"));
                }
            }

            if (attribute.Kind == UserBlockAttributeKind.Enum && attribute.AllowedValues.Count == 0)
            {
                issues.Add(Issue(
                    "USER_ELEMENTS_ENUM_WITHOUT_VALUES",
                    $"用户元素 '{definition.Id}' 的属性 '{attribute.Key}' 声明为 Enum 但未给出允许值，请至少给出一个。",
                    definition.Id,
                    "attributes"));
            }

            if (attribute.DefaultValue is not null
                && !UserBlockAttributeRules.IsValidValue(
                    attribute.Kind,
                    attribute.DefaultValue,
                    attribute.AllowedValues))
            {
                issues.Add(Issue(
                    "USER_ELEMENTS_ATTRIBUTE_DEFAULT_INVALID",
                    $"用户元素 '{definition.Id}' 的属性 '{attribute.Key}' 默认值 '{attribute.DefaultValue}' "
                    + $"不满足声明约束（{UserBlockAttributeRules.DescribeConstraint(attribute.Kind, attribute.AllowedValues)}）。",
                    definition.Id,
                    "attributes"));
            }
        }
    }

    private static void ValidateContent(
        UserBlockDefinition definition,
        List<UserBlockDefinitionEditIssue> issues)
    {
        foreach (var type in definition.Content.AllowedBlocks.Concat(definition.Content.AllowedInlines))
        {
            if (!Enum.IsDefined(type))
            {
                issues.Add(Issue(
                    "USER_ELEMENTS_CONTENT_TYPE_INVALID",
                    $"用户元素 '{definition.Id}' 的内容模型包含未知类型 '{(int)type}'，请从列表中选择。",
                    definition.Id,
                    "content"));
            }
        }
    }

    private static UserBlockDefinitionEditIssue Issue(
        string code,
        string message,
        string? definitionId,
        string field) => new()
    {
        Severity = DiagnosticSeverity.Error,
        Code = code,
        Message = message,
        DefinitionId = definitionId,
        Field = field
    };
}
