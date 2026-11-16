using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// 结构声明的规范文本：把 <see cref="UserBlockStructure"/> 写成单行可逆文本，供图常量携带。
/// v1 格式为 <c>depth:part:tag:class</c> 以 <c>|</c> 分段；v2 在前面加 <c>v2|</c>，每段追加
/// <c>mods</c> 字段（<c>na</c> 不输出 <c>data-part</c>、<c>w=条件</c>、<c>r=集合</c> 与 <c>rm=上限</c>）。
/// 槽位 token、标签、类名都已净化，不含 <c>|</c> 与 <c>:</c>，因此不需要转义。
/// 只有真正用到 v2 字段时才写 v2 前缀，历史文本与指纹保持逐字节不变。
/// </summary>
public static class UserBlockStructureText
{
    private const char Segment = '|';
    private const char Field = ':';
    private const string NoClass = "-";
    private const string V2Prefix = "v2|";
    private const string SlotsPrefix = "slots=";
    private const string NoMods = "-";
    private const string NotAddressable = "na";
    private const string RawToken = "raw";
    private const string TextPrefix = "text=";
    private const string WhenPrefix = "w=";
    private const string WhenSourcePrefix = "ws=";
    private const string RepeatPrefix = "r=";
    private const string RepeatMaxPrefix = "rm=";
    private const string GeneratorPrefix = "g=";
    private const string GeneratorParamsPrefix = "gp=";
    private const string RefPrefix = "x=";
    private const string PolicyPrefix = "p=";
    private const string HrefPrefix = "h=";
    private const string SrcPrefix = "s=";
    private const char ModSeparator = ',';

    /// <summary>写成规范文本；空结构返回空串。</summary>
    public static string Write(UserBlockStructure? structure)
    {
        if (structure?.Root is null)
        {
            return string.Empty;
        }

        var useV2 = UserBlockStructureRules.UsesSchemaV2(structure);
        var builder = new StringBuilder(96);
        if (useV2)
        {
            builder.Append(V2Prefix);

            // 自定义槽位声明放在树前的固定头段：没有声明时整段不出现，历史文本逐字节不变。
            if (structure.Slots.Count > 0)
            {
                builder.Append(SlotsPrefix)
                    .Append(string.Join(',', structure.Slots))
                    .Append(Segment);
            }
        }

        Append(builder, structure.Root, depth: 0, useV2);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, UserBlockPart part, int depth, bool useV2)
    {
        if (builder.Length > 0 && !(useV2 && builder.Length == V2Prefix.Length))
        {
            builder.Append(Segment);
        }

        builder.Append(depth.ToString(CultureInfo.InvariantCulture))
            .Append(Field).Append(part.Part)
            .Append(Field).Append(part.Tag)
            .Append(Field).Append(string.IsNullOrEmpty(part.Class) ? NoClass : part.Class);

        if (useV2)
        {
            var mods = new List<string>(2);
            if (!part.Addressable)
            {
                mods.Add(NotAddressable);
            }

            if (part.When is not null)
            {
                mods.Add(WhenPrefix + part.When);
            }

            // 源数据条件进文本：换条件必须改变语法指纹。
            if (part.WhenSource is not null)
            {
                mods.Add(WhenSourcePrefix + part.WhenSource);
            }

            // 有重复时同时写上限：上限影响输出长度，必须进文本与缓存键。
            if (part.Repeat is not null)
            {
                mods.Add(RepeatPrefix + part.Repeat);
                mods.Add(RepeatMaxPrefix + part.RepeatMax.ToString(CultureInfo.InvariantCulture));
            }

            // 生成器名与参数都进文本：换算法或换参数必须改变语法指纹。
            if (part.Generator is not null)
            {
                mods.Add(GeneratorPrefix + part.Generator);
                if (part.GeneratorParams is not null)
                {
                    mods.Add(GeneratorParamsPrefix + part.GeneratorParams);
                }
            }

            // 引用索引与安全策略都进文本：换引用值或换策略必须改变语法指纹。
            if (part.Ref is not null)
            {
                mods.Add(RefPrefix + part.Ref);
            }

            if (part.Policy is not null)
            {
                mods.Add(PolicyPrefix + part.Policy);
            }

            // 原文槽是内容源开关，进文本才能让图常量与指纹带上它。
            if (part.Raw)
            {
                mods.Add(RawToken);
            }

            if (part.Text is not null)
            {
                mods.Add(TextPrefix + part.Text);
            }

            // 受控 URL 绑定进文本：换绑定字段必须同时改变语法指纹与图渲染哈希。
            if (part.Href is not null)
            {
                mods.Add(HrefPrefix + part.Href);
            }

            if (part.Src is not null)
            {
                mods.Add(SrcPrefix + part.Src);
            }

            builder.Append(Field).Append(mods.Count == 0 ? NoMods : string.Join(ModSeparator, mods));
        }

        foreach (var child in part.Children)
        {
            Append(builder, child, depth + 1, useV2);
        }
    }

    /// <summary>解析规范文本并净化；文本不自洽时返回空结构，不抛异常。</summary>
    public static UserBlockStructure Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return UserBlockStructure.Empty;
        }

        var value = text.Trim();
        var useV2 = value.StartsWith(V2Prefix, StringComparison.Ordinal);
        if (useV2)
        {
            value = value[V2Prefix.Length..];
        }

        if (value.Length == 0)
        {
            return UserBlockStructure.Empty;
        }

        // 自定义槽位头段：只接受合法、非内建、不重复的名字；任何非法都整条拒绝，
        // 避免图常量把“未声明却在用”的槽位带进渲染。
        var slots = new List<string>();
        if (useV2 && value.StartsWith(SlotsPrefix, StringComparison.Ordinal))
        {
            var end = value.IndexOf(Segment);
            var list = end < 0 ? value[SlotsPrefix.Length..] : value[SlotsPrefix.Length..end];
            if (list.Length == 0)
            {
                return UserBlockStructure.Empty;
            }

            foreach (var token in list.Split(','))
            {
                if (!UserBlockStructureRules.TryNormalizeSlotName(token, out var name)
                    || UserBlockStructureRules.IsBuiltInSlot(name)
                    || slots.Contains(name, StringComparer.Ordinal))
                {
                    return UserBlockStructure.Empty;
                }

                slots.Add(name);
            }

            if (slots.Count > UserBlockStructureRules.MaxCustomSlots)
            {
                return UserBlockStructure.Empty;
            }

            value = end < 0 ? string.Empty : value[(end + 1)..];
            if (value.Length == 0)
            {
                return UserBlockStructure.Empty;
            }
        }

        var stack = new List<(int Depth, UserBlockPart Part, List<UserBlockPart> Children)>();
        UserBlockPart? root = null;

        foreach (var segment in value.Split(Segment, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = segment.Split(Field);
            if (fields.Length < 3
                || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth)
                || depth < 0)
            {
                return UserBlockStructure.Empty;
            }

            var addressable = true;
            string? when = null;
            string? whenSource = null;
            string? repeat = null;
            var repeatMax = UserBlockStructureRules.DefaultRepeatMax;
            string? generator = null;
            string? generatorParams = null;
            string? reference = null;
            string? policy = null;
            var raw = false;
            string? textField = null;
            string? href = null;
            string? src = null;
            if (useV2
                && !TryReadMods(
                    fields,
                    out addressable,
                    out when,
                    out whenSource,
                    out repeat,
                    out repeatMax,
                    out generator,
                    out generatorParams,
                    out reference,
                    out policy,
                    out raw,
                    out textField,
                    out href,
                    out src))
            {
                // v2 只认已声明的字段；未知 token 整条拒绝，避免静默丢弃新语义。
                return UserBlockStructure.Empty;
            }

            while (stack.Count > depth)
            {
                if (!ClosePart(stack, ref root))
                {
                    return UserBlockStructure.Empty;
                }
            }

            if (stack.Count != depth)
            {
                return UserBlockStructure.Empty;
            }

            stack.Add((
                depth,
                new UserBlockPart
                {
                    Part = fields[1],
                    Tag = fields[2],
                    Addressable = !useV2 || addressable,
                    When = useV2 ? when : null,
                    WhenSource = useV2 ? whenSource : null,
                    Repeat = useV2 ? repeat : null,
                    RepeatMax = repeatMax,
                    Generator = useV2 ? generator : null,
                    GeneratorParams = useV2 ? generatorParams : null,
                    Ref = useV2 ? reference : null,
                    Policy = useV2 ? policy : null,
                    Raw = useV2 && raw,
                    Text = useV2 ? textField : null,
                    Href = useV2 ? href : null,
                    Src = useV2 ? src : null,
                    Class = fields.Length < 4 || fields[3] == NoClass ? null : fields[3],
                },
                []));
        }

        while (stack.Count > 0)
        {
            if (!ClosePart(stack, ref root))
            {
                return UserBlockStructure.Empty;
            }
        }

        return root is null
            ? UserBlockStructure.Empty
            : UserBlockStructureRules.Sanitize(new UserBlockStructure { Root = root, Slots = slots }).Structure;
    }

    /// <summary>读 v2 段的 mods 字段；未知 token 返回 false（整条文本拒绝）。</summary>
    private static bool TryReadMods(
        string[] fields,
        out bool addressable,
        out string? when,
        out string? whenSource,
        out string? repeat,
        out int repeatMax,
        out string? generator,
        out string? generatorParams,
        out string? reference,
        out string? policy,
        out bool raw,
        out string? text,
        out string? href,
        out string? src)
    {
        addressable = true;
        when = null;
        whenSource = null;
        repeat = null;
        repeatMax = UserBlockStructureRules.DefaultRepeatMax;
        generator = null;
        generatorParams = null;
        reference = null;
        policy = null;
        raw = false;
        text = null;
        href = null;
        src = null;
        var hasMax = false;
        if (fields.Length > 5)
        {
            return false;
        }

        if (fields.Length < 5)
        {
            return true;
        }

        var mods = fields[4];
        if (mods == NoMods)
        {
            return true;
        }

        foreach (var token in mods.Split(ModSeparator))
        {
            if (token == NotAddressable)
            {
                addressable = false;
                continue;
            }

            if (token == NoMods)
            {
                continue;
            }

            if (token == RawToken)
            {
                if (raw)
                {
                    return false;
                }

                raw = true;
                continue;
            }

            if (token.StartsWith(TextPrefix, StringComparison.Ordinal))
            {
                var candidate = token[TextPrefix.Length..];
                if (text is not null || !UserBlockStructureRules.TryParseFieldTemplate(candidate, out _))
                {
                    return false;
                }

                text = candidate;
                continue;
            }

            // 受控 URL 绑定只接受纯洞模板；标签配对与字段是否已声明交给净化与保存期校验。
            if (token.StartsWith(HrefPrefix, StringComparison.Ordinal))
            {
                if (href is not null
                    || !UserBlockStructureRules.TryParseFieldTemplate(token[HrefPrefix.Length..], out var hrefField))
                {
                    return false;
                }

                href = "{" + hrefField + "}";
                continue;
            }

            if (token.StartsWith(SrcPrefix, StringComparison.Ordinal))
            {
                if (src is not null
                    || !UserBlockStructureRules.TryParseFieldTemplate(token[SrcPrefix.Length..], out var srcField))
                {
                    return false;
                }

                src = "{" + srcField + "}";
                continue;
            }

            if (token.StartsWith(WhenPrefix, StringComparison.Ordinal))
            {
                var normalized = UserBlockStructureRules.NormalizeWhen(token[WhenPrefix.Length..]);
                if (normalized is null || when is not null)
                {
                    return false;
                }

                when = normalized;
                continue;
            }

            if (token.StartsWith(WhenSourcePrefix, StringComparison.Ordinal))
            {
                if (!UserBlockStructureRules.TryNormalizeWhenSource(
                        token[WhenSourcePrefix.Length..],
                        out var normalizedSource)
                    || whenSource is not null)
                {
                    return false;
                }

                whenSource = normalizedSource;
                continue;
            }

            if (token.StartsWith(RepeatPrefix, StringComparison.Ordinal))
            {
                if (!UserBlockStructureRules.TryNormalizeRepeat(token[RepeatPrefix.Length..], out var collection)
                    || repeat is not null)
                {
                    return false;
                }

                repeat = collection;
                continue;
            }

            if (token.StartsWith(RepeatMaxPrefix, StringComparison.Ordinal))
            {
                if (!int.TryParse(
                        token[RepeatMaxPrefix.Length..],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var max)
                    || !UserBlockStructureRules.IsValidRepeatMax(max)
                    || hasMax)
                {
                    return false;
                }

                repeatMax = max;
                hasMax = true;
                continue;
            }

            if (token.StartsWith(RefPrefix, StringComparison.Ordinal))
            {
                if (!UserBlockStructureRules.TryNormalizeRef(
                        token[RefPrefix.Length..],
                        out var normalizedRef)
                    || reference is not null)
                {
                    return false;
                }

                reference = normalizedRef;
                continue;
            }

            if (token.StartsWith(PolicyPrefix, StringComparison.Ordinal))
            {
                if (!UserBlockStructureRules.TryNormalizePolicy(
                        token[PolicyPrefix.Length..],
                        out var normalizedPolicy)
                    || policy is not null)
                {
                    return false;
                }

                policy = normalizedPolicy;
                continue;
            }

            if (token.StartsWith(GeneratorParamsPrefix, StringComparison.Ordinal))
            {
                if (generatorParams is not null)
                {
                    return false;
                }

                generatorParams = token[GeneratorParamsPrefix.Length..];
                continue;
            }

            if (token.StartsWith(GeneratorPrefix, StringComparison.Ordinal))
            {
                if (!UserBlockStructureRules.TryNormalizeGenerator(
                        token[GeneratorPrefix.Length..],
                        out var normalized)
                    || generator is not null)
                {
                    return false;
                }

                generator = normalized;
                continue;
            }

            return false;
        }

        // 一个部件只有一个条件槽位：字段条件与源数据条件并存属于自相矛盾的文本。
        if (when is not null && whenSource is not null)
        {
            return false;
        }

        // 上限不能脱离重复声明单独存在，避免“无重复却带 max”的隐式语义。
        if (hasMax && repeat is null)
        {
            return false;
        }

        // 参数不能脱离生成器单独存在；参数合法性交给净化与保存校验（这里只做语法归属）。
        return generatorParams is null || generator is not null;
    }

    private static bool ClosePart(
        List<(int Depth, UserBlockPart Part, List<UserBlockPart> Children)> stack,
        ref UserBlockPart? root)
    {
        var current = stack[^1];
        stack.RemoveAt(stack.Count - 1);

        var completed = current.Part with { Children = current.Children };
        if (stack.Count == 0)
        {
            if (root is not null)
            {
                return false;
            }

            root = completed;
            return true;
        }

        stack[^1].Children.Add(completed);
        return true;
    }
}

/// <summary>
/// 结构声明与图部件树之间的双向映射。两侧只表达“标签 + 槽位 + 类名 + 子部件”，
/// 不引入第二套结构语义。
/// </summary>
public static class UserBlockStructureGraph
{
    /// <summary>结构声明 → 图部件树；<paramref name="markCustom"/> 决定发射阶段是否据此换根。</summary>
    public static IReadOnlyList<GraphPart> ToParts(UserBlockStructure? structure, bool markCustom = true)
    {
        if (structure?.Root is null)
        {
            return [];
        }

        return [ToPart(structure.Root, markCustom)];
    }

    /// <summary>图常量携带的文本 → 图值；空结构返回 None。</summary>
    public static GraphValue ToValue(string? raw)
    {
        var parts = ToParts(UserBlockStructureText.Parse(raw));
        return parts.Count == 0 ? GraphValue.None : GraphValue.FromParts(parts);
    }

    private static GraphPart ToPart(UserBlockPart part, bool markCustom)
    {
        var slot = part.Part switch
        {
            "icon" => GraphPartSlot.Icon,
            "title-text" => GraphPartSlot.TitleText,
            "body" or "content" => GraphPartSlot.Children,
            _ => GraphPartSlot.None,
        };

        var role = part.Part switch
        {
            "root" => ElementPartRole.Root,
            "icon" => ElementPartRole.Icon,
            "title" or "title-text" => ElementPartRole.Title,
            "footer" => ElementPartRole.Tail,
            "badge" or "meta" => ElementPartRole.Meta,
            _ => ElementPartRole.Content,
        };

        // 标签插值（schema v2）按块属性展开；展开求值在渲染期完成。
        // 图里保留原始插值文本（TagTemplate），静态标签才放 Tag，绝不把 "{field}" 当标签直出。
        var isTagExpression = UserBlockStructureRules.IsTagExpression(part.Tag);
        var tag = isTagExpression ? null : part.Tag;

        // 条件类名（v2）同样在渲染期求值；图里保留原始 class 串，
        // CssClass 只放无条件 token 供检查器展示，绝不把条件文法写进 class 字节。
        var rawClass = part.Class;
        var cssClass = UserBlockStructureRules.StripConditionalClasses(rawClass) ?? string.Empty;

        return new GraphPart(
            part.Part,
            tag,
            cssClass,
            part.Children.Select(child => ToPart(child, markCustom)).ToList(),
            Role: role,
            Slot: slot,
            IsCustom: markCustom,
            TagTemplate: isTagExpression ? part.Tag : null,
            RawClass: rawClass,
            Addressable: part.Addressable,
            When: part.When,
            WhenSource: part.WhenSource,
            Repeat: part.Repeat,
            RepeatMax: part.RepeatMax,
            Generator: part.Generator,
            GeneratorParams: part.GeneratorParams,
            Ref: part.Ref,
            Policy: part.Policy,
            Raw: part.Raw,
            Text: part.Text,
            Href: part.Href,
            Src: part.Src);
    }

    /// <summary>图部件树 → 结构声明；多个顶层部件补一个 <c>div</c> 根，与发射阶段的规则一致。</summary>
    public static UserBlockStructure FromParts(IReadOnlyList<GraphPart>? parts)
    {
        if (parts is null || parts.Count == 0)
        {
            return UserBlockStructure.Empty;
        }

        var root = parts.Count == 1
            ? parts[0]
            : new GraphPart(
                UserBlockStructureRules.WrapperPart,
                "div",
                string.Empty,
                parts.ToList(),
                Role: ElementPartRole.Root,
                IsCustom: true);

        // 图里只保留实际用到的槽名；自定义槽位声明按树中出现顺序重建，
        // 保证图 → 结构的往返不会把自定义槽位判成未声明。
        var rootPart = FromPart(root);
        var slots = new List<string>();
        CollectCustomSlots(rootPart, slots);

        return UserBlockStructureRules.Sanitize(
            new UserBlockStructure { Root = rootPart, Slots = slots }).Structure;
    }

    private static void CollectCustomSlots(UserBlockPart part, List<string> slots)
    {
        if (UserBlockStructureRules.TryNormalizeSlotName(part.Part, out var name)
            && !UserBlockStructureRules.IsBuiltInSlot(name)
            && !slots.Contains(name, StringComparer.Ordinal))
        {
            slots.Add(name);
        }

        foreach (var child in part.Children)
        {
            CollectCustomSlots(child, slots);
        }
    }

    private static UserBlockPart FromPart(GraphPart part)
    {
        var children = new List<UserBlockPart>(part.Children.Count);
        foreach (var child in part.Children)
        {
            children.Add(FromPart(child));
        }

        return new UserBlockPart
        {
            Part = string.IsNullOrWhiteSpace(part.Name) ? "content" : part.Name,
            Tag = part.TagTemplate ?? part.Tag ?? "div",
            Class = part.RawClass ?? (string.IsNullOrWhiteSpace(part.CssClass) ? null : part.CssClass),
            Addressable = part.Addressable,
            When = part.When,
            WhenSource = part.WhenSource,
            Repeat = part.Repeat,
            RepeatMax = part.RepeatMax,
            Generator = part.Generator,
            GeneratorParams = part.GeneratorParams,
            Ref = part.Ref,
            Policy = part.Policy,
            Raw = part.Raw,
            Text = part.Text,
            Href = part.Href,
            Src = part.Src,
            Children = children,
        };
    }
}
