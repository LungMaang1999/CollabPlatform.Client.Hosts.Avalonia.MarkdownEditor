using System.Text.RegularExpressions;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 用户块定义注册表。构造即校验，非法定义在加载期抛出，不进入解析期。
/// </summary>
public sealed class UserBlockRegistry
{
    /// <summary>默认围栏字符：冒号族，现有语法未占用。</summary>
    public const char SupportedFenceChar = ':';

    /// <summary>第二围栏族：三单引号，与内置诗词/时间线/雷达图共用围栏符，靠关键字区分。</summary>
    public const char PrimeFenceChar = '\'';

    /// <summary>代码围栏族（反引号），与 Markdig <c>FencedCodeBlock</c> 共用围栏符，靠关键字区分。</summary>
    public const char BacktickFenceChar = '`';

    /// <summary>代码围栏族（波浪号），语义同反引号，只是围栏字符不同。</summary>
    public const char TildeFenceChar = '~';

    /// <summary>旧码：字符与正文无法区分（空白、字母、数字、控制字符）。</summary>
    public const string InvalidFenceCharCode = "USER_ELEMENTS_FENCE_CHAR_CONFLICT";

    /// <summary>新码：字符是 Markdown 块起始符，已被核心语法占用。</summary>
    public const string ReservedFenceCharCode = "USER_ELEMENTS_FENCE_CHAR_RESERVED";

    /// <summary>
    /// Markdown 块起始符保留表：这些字符在行首会先被核心语法匹配，
    /// 声明成围栏符要么永远命中不了，要么抢掉标题/列表/引用/表格等既有语法。
    /// 值是该字符占用的核心语法，诊断文案直接引用，避免“为什么不行”需要翻代码。
    /// </summary>
    public static readonly IReadOnlyDictionary<char, string> ReservedFenceChars =
        new Dictionary<char, string>
        {
            ['#'] = "ATX 标题",
            ['>'] = "块引用",
            ['-'] = "无序列表 / 分隔线 / setext 标题下划线",
            ['+'] = "无序列表",
            ['*'] = "无序列表 / 分隔线",
            ['_'] = "分隔线",
            ['|'] = "表格",
            ['['] = "链接引用定义",
            ['<'] = "HTML 块",
        };

    /// <summary>围栏字符的统一规则说明；所有校验入口共用一句话，避免各处口径漂移。</summary>
    public static readonly string FenceCharRule =
        "围栏字符必须是单个非空白、非字母、非数字字符，且不能是 Markdown 块起始符："
        + string.Join("、", ReservedFenceChars.Select(item => $"'{item.Key}'（{item.Value}）"));

    public static bool IsReservedFenceChar(char fenceChar) => ReservedFenceChars.ContainsKey(fenceChar);

    /// <summary>
    /// 定义可使用的围栏字符：任意非空白、非字母、非数字、非控制字符，
    /// 且不在 Markdown 块起始符保留表内。具体哪些字符真正生效由定义声明决定
    /// （解析器只为已声明的字符注册）。
    /// </summary>
    public static bool IsSupportedFenceChar(char fenceChar) =>
        !char.IsWhiteSpace(fenceChar)
        && !char.IsLetterOrDigit(fenceChar)
        && !char.IsControl(fenceChar)
        && !IsReservedFenceChar(fenceChar);

    /// <summary>
    /// 围栏字符的失败原因：保留字符报 <see cref="ReservedFenceCharCode"/>，
    /// 其余不可用字符报 <see cref="InvalidFenceCharCode"/>。可用时返回 true。
    /// </summary>
    public static bool TryGetFenceCharIssue(char fenceChar, out string code, out string detail)
    {
        if (IsReservedFenceChar(fenceChar))
        {
            code = ReservedFenceCharCode;
            detail = $"'{fenceChar}' 是 Markdown 块起始符（{ReservedFenceChars[fenceChar]}），"
                + "行首会先被核心语法匹配，不能声明为围栏字符";
            return false;
        }

        if (char.IsWhiteSpace(fenceChar) || char.IsLetterOrDigit(fenceChar) || char.IsControl(fenceChar))
        {
            code = InvalidFenceCharCode;
            detail = $"'{fenceChar}' 与正文无法区分；{FenceCharRule}";
            return false;
        }

        code = string.Empty;
        detail = string.Empty;
        return true;
    }

    /// <summary>
    /// 围栏字符对应的内置关键字族；冒号族没有内置关键字（全部可用），
    /// 单引号族与代码围栏族受各自内置保留字约束。
    /// </summary>
    public static FenceFamily? FamilyOf(char fenceChar) => fenceChar switch
    {
        PrimeFenceChar => FenceFamily.PrimeFence,
        BacktickFenceChar or TildeFenceChar => FenceFamily.CodeFence,
        _ => null
    };

    public const int MinFenceLength = 3;
    public const int MaxFenceLength = 12;
    public const int MinPriority = -1000;
    public const int MaxPriority = 1000;

    /// <summary>开围栏阶段的嵌套上限；超过后开围栏降级为正文。</summary>
    public const int MaxNestingDepth = 32;

    private static readonly Regex IdentifierPattern = new(
        "^[A-Za-z][A-Za-z0-9_-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly List<UserBlockDefinition> _definitions;

    public UserBlockRegistry(IEnumerable<UserBlockDefinition>? definitions = null)
    {
        _definitions = [];
        if (definitions is null) return;

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claims = new KeywordClaims();
        foreach (var definition in definitions)
        {
            // 内置类型重写定义只作用于渲染描述：没有围栏、没有关键字，
            // 不能进入解析规则表，否则会用类型名占住 ::: 关键字。
            if (definition.AppliesTo is not null) continue;

            Validate(definition, ids, claims);
            _definitions.Add(definition);
        }
    }

    public IReadOnlyList<UserBlockDefinition> Definitions => _definitions;

    public bool IsEmpty => _definitions.Count == 0;

    /// <summary>所有定义声明过的围栏字符（去重）。解析器只为这些字符注册，新增字符只加数据。</summary>
    public IReadOnlyList<char> DeclaredFenceChars =>
        _definitions.SelectMany(item => item.EffectiveFenceChars).Distinct().ToList();

    /// <summary>Id / 名称 / 属性键共用的标识符规则（加载器复用同一判定）。</summary>
    public static bool IsValidIdentifier(string? value) =>
        !string.IsNullOrEmpty(value) && IdentifierPattern.IsMatch(value);

    /// <summary>
    /// 解析一段围栏后的文本（如 <c>:::note level=info</c> 的 <c>note level=info</c>）。
    /// 关键字必须整词匹配，避免 note 吃掉 note-x；别名与本名同等参与匹配，
    /// <paramref name="keywordLength"/> 返回实际命中的关键字长度（本名与别名可不等长）。
    /// </summary>
    internal bool TryResolve(
        string headerAfterFence,
        int fenceLength,
        char fenceChar,
        out UserBlockDefinition definition,
        out int keywordLength)
    {
        definition = null!;
        keywordLength = 0;
        if (!IsSupportedFenceChar(fenceChar)) return false;
        if (fenceLength < MinFenceLength) return false;

        var span = headerAfterFence.AsSpan().TrimStart();
        if (span.IsEmpty) return false;

        var nameLength = 0;
        while (nameLength < span.Length && !char.IsWhiteSpace(span[nameLength]))
        {
            nameLength++;
        }

        var name = span[..nameLength].ToString();
        if (name.Length == 0) return false;

        UserBlockDefinition? best = null;
        foreach (var item in _definitions)
        {
            // 同名跨族互不干扰：每个围栏族各自解析，不会一方抢走另一方的围栏；
            // 多字符定义在它声明的每个围栏字符下都可命中。
            if (!item.UsesFenceChar(fenceChar)) continue;
            // 别名与本名同一优先级：命中任一关键字都落到同一元素。
            if (!item.MatchesKeyword(name)) continue;
            if (fenceLength < item.FenceLength) continue;

            if (best is null
                || item.Priority > best.Priority
                || (item.Priority == best.Priority && item.FenceLength > best.FenceLength))
            {
                best = item;
            }
        }

        if (best is null) return false;
        definition = best;
        keywordLength = nameLength;
        return true;
    }

    private static void Validate(UserBlockDefinition definition, HashSet<string> ids, KeywordClaims claims)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            throw new UserBlockDefinitionException("用户块定义缺少 Id。");
        }

        if (!IdentifierPattern.IsMatch(definition.Id))
        {
            throw new UserBlockDefinitionException(
                $"用户块 Id '{definition.Id}' 非法：需字母开头，仅含字母、数字、下划线或连字符，最长 64。");
        }

        if (!ids.Add(definition.Id))
        {
            throw new UserBlockDefinitionException($"用户块 Id '{definition.Id}' 重复。");
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new UserBlockDefinitionException($"用户块 '{definition.Id}' 缺少开围栏名称。");
        }

        if (!IdentifierPattern.IsMatch(definition.Name))
        {
            throw new UserBlockDefinitionException(
                $"用户块 '{definition.Id}' 的名称 '{definition.Name}' 非法：需字母开头，仅含字母、数字、下划线或连字符，最长 64。");
        }

        if (!TryGetFenceCharIssue(definition.FenceChar, out _, out var fenceDetail))
        {
            throw new UserBlockDefinitionException(
                $"用户块 '{definition.Id}' 的围栏字符不可用；{fenceDetail}。");
        }

        // 多字符声明：首项必须与旧字段一致，避免同一份定义出现两个互相矛盾的事实来源。
        if (definition.FenceChars.Count > 0)
        {
            if (definition.FenceChars[0] != definition.FenceChar)
            {
                throw new UserBlockDefinitionException(
                    $"用户块 '{definition.Id}' 的多围栏声明首项 '{definition.FenceChars[0]}' "
                    + $"与 fence-char '{definition.FenceChar}' 不一致。");
            }

            var declared = new HashSet<char>();
            foreach (var fenceChar in definition.FenceChars)
            {
                if (!TryGetFenceCharIssue(fenceChar, out _, out var fenceIssueDetail))
                {
                    throw new UserBlockDefinitionException(
                        $"用户块 '{definition.Id}' 的多围栏声明不可用；{fenceIssueDetail}。");
                }

                if (!declared.Add(fenceChar))
                {
                    throw new UserBlockDefinitionException(
                        $"用户块 '{definition.Id}' 的多围栏声明含重复字符 '{fenceChar}'。");
                }
            }
        }

        // 内置关键字是保留字：任一已声明的围栏族撞车都拒绝，否则会静默顶掉内置围栏。
        foreach (var fenceChar in definition.EffectiveFenceChars.Distinct())
        {
            if (FamilyOf(fenceChar) is { } family
                && FenceKeywordRegistry.BuiltIn.IsReservedKeyword(family, definition.Name))
            {
                throw new UserBlockDefinitionException(
                    $"用户块 '{definition.Id}' 的名称 '{definition.Name}' 是内置围栏关键字，"
                    + "不能作为元素名称。");
            }
        }

        // 别名：数量、格式、与本名相同、同一定义内重复逐条拒绝，不做静默去重。
        if (definition.Aliases.Count > UserBlockDefinition.MaxAliases)
        {
            throw new UserBlockDefinitionException(
                $"用户块 '{definition.Id}' 声明了 {definition.Aliases.Count} 个别名，"
                + $"超过上限 {UserBlockDefinition.MaxAliases}。");
        }

        var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in definition.Aliases)
        {
            if (alias is null || !IdentifierPattern.IsMatch(alias))
            {
                throw new UserBlockDefinitionException(
                    $"用户块 '{definition.Id}' 的别名 '{alias}' 非法：需字母开头，仅含字母、数字、下划线或连字符，最长 64。");
            }

            if (string.Equals(alias, definition.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new UserBlockDefinitionException(
                    $"用户块 '{definition.Id}' 的别名 '{alias}' 与本名相同，必须移除该别名。");
            }

            if (!seenAliases.Add(alias))
            {
                throw new UserBlockDefinitionException(
                    $"用户块 '{definition.Id}' 的别名 '{alias}' 重复声明。");
            }

            foreach (var fenceChar in definition.EffectiveFenceChars.Distinct())
            {
                if (FamilyOf(fenceChar) is { } family
                    && FenceKeywordRegistry.BuiltIn.IsReservedKeyword(family, alias))
                {
                    throw new UserBlockDefinitionException(
                        $"用户块 '{definition.Id}' 的别名 '{alias}' 是内置围栏关键字，不能作为别名。");
                }
            }
        }

        // 别名的定义间唯一性：同一围栏字符下，别名不得撞任何其他定义的本名或别名；
        // 本名之间的重复仍由 priority 仲裁，历史行为保持不变。
        foreach (var fenceChar in definition.EffectiveFenceChars.Distinct())
        {
            claims.ClaimName(fenceChar, definition.Name, definition.Id);
        }

        foreach (var alias in definition.Aliases)
        {
            foreach (var fenceChar in definition.EffectiveFenceChars.Distinct())
            {
                claims.ClaimAlias(fenceChar, alias, definition.Id);
            }
        }

        var attributeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in definition.Attributes)
        {
            if (!IsValidIdentifier(attribute.Key))
            {
                throw new UserBlockDefinitionException(
                    $"用户块 '{definition.Id}' 的属性键 '{attribute.Key}' 非法：需字母开头，仅含字母、数字、下划线或连字符，最长 64。");
            }

            if (!attributeKeys.Add(attribute.Key))
            {
                throw new UserBlockDefinitionException(
                    $"用户块 '{definition.Id}' 的属性键 '{attribute.Key}' 重复。");
            }

            var allowedValues = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in attribute.AllowedValues)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new UserBlockDefinitionException(
                        $"用户块 '{definition.Id}' 的属性 '{attribute.Key}' 含空允许值。");
                }

                if (!allowedValues.Add(value.Trim()))
                {
                    throw new UserBlockDefinitionException(
                        $"用户块 '{definition.Id}' 的属性 '{attribute.Key}' 的允许值 '{value}' 重复。");
                }
            }
        }

        if (definition.FenceLength is < MinFenceLength or > MaxFenceLength)
        {
            throw new UserBlockDefinitionException(
                $"用户块 '{definition.Id}' 的围栏长度 {definition.FenceLength} 越界：允许 {MinFenceLength}~{MaxFenceLength}。");
        }

        if (definition.Priority is < MinPriority or > MaxPriority)
        {
            throw new UserBlockDefinitionException(
                $"用户块 '{definition.Id}' 的优先级 {definition.Priority} 越界：允许 {MinPriority}~{MaxPriority}。");
        }
    }

    /// <summary>
    /// 关键字占用表：按「围栏字符 + 关键字」记录归属，防止别名静默顶掉别的定义。
    /// 本名之间允许重复（由 priority 仲裁），别名与任何已有本名/别名重复都拒绝。
    /// </summary>
    private sealed class KeywordClaims
    {
        private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

        public void ClaimName(char fenceChar, string keyword, string definitionId)
        {
            var key = Key(fenceChar, keyword);
            if (_aliases.TryGetValue(key, out var owner))
            {
                throw new UserBlockDefinitionException(
                    $"用户块 '{definitionId}' 的名称 '{keyword}' 与定义 '{owner}' 的别名重复；"
                    + "别名不能覆盖其他定义的关键字。");
            }

            _names[key] = definitionId;
        }

        public void ClaimAlias(char fenceChar, string keyword, string definitionId)
        {
            var key = Key(fenceChar, keyword);
            if (_names.TryGetValue(key, out var owner))
            {
                throw new UserBlockDefinitionException(
                    $"用户块 '{definitionId}' 的别名 '{keyword}' 与定义 '{owner}' 的关键字重复；"
                    + "别名不能覆盖其他定义的关键字。");
            }

            if (_aliases.TryGetValue(key, out owner))
            {
                throw new UserBlockDefinitionException(
                    $"用户块 '{definitionId}' 的别名 '{keyword}' 与定义 '{owner}' 的别名重复。");
            }

            _aliases[key] = definitionId;
        }

        private static string Key(char fenceChar, string keyword) => fenceChar + "\u0000" + keyword;
    }
}
