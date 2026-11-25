using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>围栏符族：同一个关键字在不同围栏符下可以落到不同类型。</summary>
public enum FenceFamily
{
    /// <summary>反引号/波浪号代码围栏，经 Markdig <c>FencedCodeBlock</c> 交给转换器。</summary>
    CodeFence,

    /// <summary>三个单引号的自定义内容围栏，由块解析器直接建块。</summary>
    PrimeFence
}

/// <summary>围栏关键字最终落到的类型。</summary>
public enum FenceTarget
{
    Math,
    Mermaid,
    ImageMatrix,
    QrCode,
    Poem,
    Timeline,
    Radar
}

/// <summary>
/// 一条围栏关键字声明。<see cref="Keyword"/> 为空表示只认围栏符、不认关键字的裸围栏；
/// <see cref="CanonicalKeyword"/> 指向同族的规范关键字，用于别名多对一。
/// </summary>
public sealed record FenceKeywordEntry(
    string Keyword,
    FenceFamily Family,
    FenceTarget Target,
    string? CanonicalKeyword = null,
    PoemType? PoemType = null)
{
    public bool IsBare => Keyword.Length == 0;
}

/// <summary>
/// 围栏关键字的运行时注册表：查表字典在构造期按声明集合重建，不是编译期常量表。
/// 内置声明只是 <see cref="BuiltInEntries"/> 这一份“来源清单”，
/// 管线每次创建运行时都会用 <see cref="FenceKeywordRegistry(IEnumerable{FenceKeywordEntry})"/>
/// 重新构建自己的实例，后续引入定义贡献的关键字时无需再改查表逻辑。
/// </summary>
public sealed class FenceKeywordRegistry
{
    /// <summary>内置关键字的来源声明。唯一的编译期清单，只提供数据、不提供查表。</summary>
    private static readonly FenceKeywordEntry[] Declared =
    {
        // 反引号/波浪号围栏（Markdig FencedCodeBlock）
        new("math", FenceFamily.CodeFence, FenceTarget.Math),
        new("latex", FenceFamily.CodeFence, FenceTarget.Math, "math"),
        new("mermaid", FenceFamily.CodeFence, FenceTarget.Mermaid),
        new("image", FenceFamily.CodeFence, FenceTarget.ImageMatrix),
        new("img", FenceFamily.CodeFence, FenceTarget.ImageMatrix, "image"),
        new("qrcode", FenceFamily.CodeFence, FenceTarget.QrCode),
        new("qr", FenceFamily.CodeFence, FenceTarget.QrCode, "qrcode"),

        // 三单引号围栏
        new("ancient-poetry", FenceFamily.PrimeFence, FenceTarget.Poem, PoemType: PoemType.AncientPoem),
        new("ancient-poem", FenceFamily.PrimeFence, FenceTarget.Poem, "ancient-poetry", PoemType.AncientPoem),
        new("poetry", FenceFamily.PrimeFence, FenceTarget.Poem, PoemType: PoemType.General),
        new("poem", FenceFamily.PrimeFence, FenceTarget.Poem, "poetry", PoemType: PoemType.General),
        new("verse", FenceFamily.PrimeFence, FenceTarget.Poem, "poetry", PoemType: PoemType.General),
        new("shi", FenceFamily.PrimeFence, FenceTarget.Poem, PoemType: PoemType.Shi),
        new("ci", FenceFamily.PrimeFence, FenceTarget.Poem, PoemType: PoemType.Ci),
        new("qu", FenceFamily.PrimeFence, FenceTarget.Poem, PoemType: PoemType.Qu),
        new("image", FenceFamily.PrimeFence, FenceTarget.ImageMatrix),
        new("img", FenceFamily.PrimeFence, FenceTarget.ImageMatrix, "image"),
        new("timeline", FenceFamily.PrimeFence, FenceTarget.Timeline),
        new("radar", FenceFamily.PrimeFence, FenceTarget.Radar),
        new("spider", FenceFamily.PrimeFence, FenceTarget.Radar, "radar"),
    };

    /// <summary>内置声明清单（只读）。调用方可据此重建注册表，也可在测试里替换声明集合。</summary>
    public static IReadOnlyList<FenceKeywordEntry> BuiltInEntries => Declared;

    /// <summary>内置注册表实例，供不持有运行时上下文的入口使用。</summary>
    public static FenceKeywordRegistry BuiltIn { get; } = new(Declared);

    /// <summary>
    /// 按本次运行时的定义集合重建关键字表：内置声明在前，定义声明的首词追加在后。
    /// 定义只贡献 <see cref="UserBlockDefinition.Aliases"/>（<c>keywords</c>）——
    /// <see cref="UserBlockDefinition.Name"/> 对 <c>applies-to</c> 定义是类型名，不是关键字。
    /// 同族同关键字已存在时保持先声明者（内置优先），不静默改写既有形态。
    /// </summary>
    public static FenceKeywordRegistry Create(IEnumerable<UserBlockDefinition>? definitions)
    {
        if (definitions is null)
        {
            return BuiltIn;
        }

        List<FenceKeywordEntry>? merged = null;
        foreach (var definition in definitions)
        {
            if (!FenceOpenTargets.TryGetTarget(definition.Opens, out var target))
            {
                continue;
            }

            foreach (var fenceChar in definition.EffectiveFenceChars)
            {
                if (!FenceOpenTargets.SupportsFenceChar(definition.Opens, fenceChar))
                {
                    continue;
                }

                var family = UserBlockRegistry.FamilyOf(fenceChar);
                if (family is null)
                {
                    continue;
                }

                foreach (var keyword in definition.Aliases)
                {
                    if (string.IsNullOrWhiteSpace(keyword))
                    {
                        continue;
                    }

                    merged ??= [.. Declared];
                    if (Contains(merged, family.Value, keyword))
                    {
                        continue;
                    }

                    merged.Add(new FenceKeywordEntry(
                        keyword,
                        family.Value,
                        target,
                        PoemType: target == FenceTarget.Poem ? PoemType.General : null));
                }
            }
        }

        return merged is null ? BuiltIn : new FenceKeywordRegistry(merged);
    }

    private static bool Contains(
        IReadOnlyList<FenceKeywordEntry> entries,
        FenceFamily family,
        string keyword)
    {
        foreach (var entry in entries)
        {
            if (entry.Family == family
                && string.Equals(entry.Keyword, keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private readonly FenceKeywordEntry[] _entries;
    private readonly Dictionary<(FenceFamily Family, string Keyword), FenceKeywordEntry> _exact;

    public FenceKeywordRegistry(IEnumerable<FenceKeywordEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries.ToArray();
        _exact = Build(_entries);
    }

    public IReadOnlyList<FenceKeywordEntry> Entries => _entries;

    /// <summary>按整词解析（代码围栏的 info 首词）。</summary>
    public FenceKeywordEntry? ResolveExact(FenceFamily family, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return _exact.TryGetValue((family, token.ToUpperInvariant()), out var entry) ? entry : null;
    }

    /// <summary>按关键字前缀解析，要求关键字后是空白或行尾（三单引号围栏的匹配口径）。</summary>
    public bool TryMatchPrefix(
        FenceFamily family,
        string text,
        out FenceKeywordEntry entry,
        out int keywordLength)
    {
        entry = null!;
        keywordLength = 0;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var candidate in _entries)
        {
            if (candidate.Family != family || candidate.IsBare)
            {
                continue;
            }

            if (!text.StartsWith(candidate.Keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var next = candidate.Keyword.Length < text.Length
                ? text[candidate.Keyword.Length]
                : '\0';
            if (next == '\0' || char.IsWhiteSpace(next))
            {
                entry = candidate;
                keywordLength = candidate.Keyword.Length;
                return true;
            }
        }

        return false;
    }

    /// <summary>取该围栏族的裸围栏声明；未声明则返回 null。</summary>
    public FenceKeywordEntry? ResolveBare(FenceFamily family)
    {
        foreach (var candidate in _entries)
        {
            if (candidate.Family == family && candidate.IsBare)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 由专用块解析器实现、不登记到关键字表的内置三单引号语法。
    /// 它们不参与关键字→目标的查表，但仍占用关键字：定义层不能顶掉，
    /// 围栏状态扫描也必须把它们当作围栏起始（否则未闭合时定位不到行）。
    /// </summary>
    private static readonly HashSet<string> ReservedPrimeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "collapse",
        "tocpic",
        "color",
    };

    /// <summary>文本首词是否为保留的三单引号关键字（后接空白或行尾）。</summary>
    public static bool IsReservedPrimeKeyword(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var keyword in ReservedPrimeKeywords)
        {
            if (!text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (text.Length == keyword.Length || char.IsWhiteSpace(text[keyword.Length]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 该关键字是否已被该围栏族的内置声明占用。定义层据此把内置关键字当保留字拒绝，
    /// 避免定义把内置关键字顶掉（同名跨族不冲突，因为是按族判定）。
    /// </summary>
    public bool IsReservedKeyword(FenceFamily family, string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return false;
        }

        // 由专用块解析器实现、不登记到关键字表的内置语法，仍然占用关键字。
        if (family == FenceFamily.PrimeFence
            && ReservedPrimeKeywords.Contains(keyword.Trim()))
        {
            return true;
        }

        return _exact.ContainsKey((family, keyword.ToUpperInvariant()));
    }

    private static Dictionary<(FenceFamily Family, string Keyword), FenceKeywordEntry> Build(
        IReadOnlyList<FenceKeywordEntry> entries)
    {
        var map = new Dictionary<(FenceFamily Family, string Keyword), FenceKeywordEntry>();
        var bareFamilies = new HashSet<FenceFamily>();

        foreach (var entry in entries)
        {
            if (entry.IsBare)
            {
                if (!bareFamilies.Add(entry.Family))
                {
                    throw new InvalidOperationException(
                        $"围栏族 {entry.Family} 只能声明一条裸围栏。");
                }

                continue;
            }

            var key = (entry.Family, entry.Keyword.ToUpperInvariant());
            if (!map.TryAdd(key, entry))
            {
                throw new InvalidOperationException(
                    $"围栏关键字重复：{entry.Family} / \"{entry.Keyword}\"，不允许后者覆盖前者。");
            }
        }

        foreach (var entry in entries)
        {
            if (entry.CanonicalKeyword is null)
            {
                continue;
            }

            var key = (entry.Family, entry.CanonicalKeyword.ToUpperInvariant());
            if (!map.TryGetValue(key, out var canonical))
            {
                throw new InvalidOperationException(
                    $"别名 \"{entry.Keyword}\" 指向的规范关键字 \"{entry.CanonicalKeyword}\" 未在同一围栏族声明。");
            }

            if (canonical.Target != entry.Target)
            {
                throw new InvalidOperationException(
                    $"别名 \"{entry.Keyword}\" 与规范关键字 \"{entry.CanonicalKeyword}\" 的目标类型不一致。");
            }
        }

        foreach (var entry in entries)
        {
            var isPoem = entry.Target == FenceTarget.Poem;
            if (isPoem && entry.PoemType is null)
            {
                throw new InvalidOperationException(
                    $"诗体围栏 \"{entry.Keyword}\" 必须声明 PoemType。");
            }

            if (!isPoem && entry.PoemType is not null)
            {
                throw new InvalidOperationException(
                    $"非诗体围栏 \"{entry.Keyword}\" 不能声明 PoemType。");
            }
        }

        return map;
    }
}
