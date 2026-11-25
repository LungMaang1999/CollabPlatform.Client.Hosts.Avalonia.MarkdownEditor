using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>
/// 文档级标题锚点：在整棵节点树落定后统一计算，避免增量解析的切片各自编号。
/// 标题按纯文本规范化出 slug（首次出现就是 slug，之后依次 slug-1、slug-2……），
/// 同名判定按规范化后的 slug 相等；同名出现 ≥2 次时每组写入 1 起的角标序号。
/// 站内锚点链接 <c>[文字](#标题)</c> 在同一遍里改写成 <c>#slug</c>。
/// </summary>
internal static class HeadingAnchorResolver
{
    /// <summary>对整棵树重算锚点；离屏切片解析不在切片内调用，只在最终树上调用一次。</summary>
    public static void Apply(MarkdownNode root, ICollection<DiagnosticMessage>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        var headings = root.Descendants()
            .Where(node => node.Type == NodeType.Heading)
            .ToList();

        var firstTargets = new Dictionary<string, string>(StringComparer.Ordinal);
        var groupSizes = new Dictionary<string, int>(StringComparer.Ordinal);
        var baseSlugs = new string[headings.Count];
        for (int i = 0; i < headings.Count; i++)
        {
            var slug = Normalize(headings[i].Text);
            if (slug.Length == 0)
            {
                // 纯标点/空标题：用块起点偏移兜底，增量复用下仍然稳定。
                slug = headings[i].Range.StartOffset >= 0
                    ? "section-" + headings[i].Range.StartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "section-" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            baseSlugs[i] = slug;
            groupSizes[slug] = groupSizes.TryGetValue(slug, out var count) ? count + 1 : 1;
        }

        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        // 每个同名组按出现顺序记录真实 id：首个是 slug，其后是 slug-1、slug-2……
        // 链接侧据此展开 1..n 个跳转入口，不按后缀反推（撞号顺延后后缀可能不是 n-1）。
        var groupIds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (int i = 0; i < headings.Count; i++)
        {
            var baseSlug = baseSlugs[i];
            var occurrence = occurrences.TryGetValue(baseSlug, out var seen) ? seen + 1 : 1;
            occurrences[baseSlug] = occurrence;

            // 首次出现直接用 slug，第 n 次（n≥2）加 n-1 后缀：slug、slug-1、slug-2……
            var id = occurrence == 1
                ? baseSlug
                : baseSlug + "-" + (occurrence - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

            // 标题文本本身可能已经长得像后缀（例如 a-1），撞号时顺延到第一个空位。
            if (!usedIds.Add(id))
            {
                var suffix = occurrence - 1;
                do
                {
                    suffix++;
                    id = baseSlug + "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                while (!usedIds.Add(id));
            }

            var heading = headings[i];
            heading.SetDerivedAttribute("slug", id);
            if (groupSizes[baseSlug] >= 2)
            {
                heading.SetDerivedAttribute(
                    "anchor-index",
                    occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                // 改名去重后可能从同名组里“毕业”，必须清掉旧角标，渲染层按缺席判定。
                heading.RemoveAttribute("anchor-index");
            }

            firstTargets.TryAdd(baseSlug, id);

            if (!groupIds.TryGetValue(baseSlug, out var ids))
            {
                ids = [];
                groupIds[baseSlug] = ids;
            }

            ids.Add(id);
        }

        if (headings.Count == 0)
        {
            return;
        }

        foreach (var link in root.Descendants().Where(node => node.Type == NodeType.Link).ToList())
        {
            if (!link.Attributes.TryGetValue("href", out var href))
            {
                continue;
            }

            href = href.Trim();
            if (href.Length < 2 || href[0] != '#')
            {
                continue;
            }

            var fragment = TryDecode(href[1..]);
            var slug = Normalize(fragment);
            if (slug.Length > 0 && firstTargets.TryGetValue(slug, out var target))
            {
                // href 给展示层，url 是渲染绑定的同源镜像，两者一起改写。
                link.SetDerivedAttribute("href", "#" + target);
                link.SetDerivedAttribute("url", "#" + target);
                ApplyGroup(link, groupIds[slug]);
                continue;
            }

            // 旧写法 [文字](#标题-2) 显式指向某一处：命中的是标题 id 而非同名组，
            // 保留原 href，也不展开编号。
            if (slug.Length > 0 && usedIds.Contains(slug))
            {
                ClearGroup(link);
                continue;
            }

            ClearGroup(link);
            diagnostics?.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Warning,
                Code = "UNRESOLVED_ANCHOR",
                Message = $"未找到与「{fragment}」同名的标题，锚点保持原样。",
                Range = link.Range
            });
        }
    }

    /// <summary>
    /// 同名组 ≥2 时把「组内全部 id」写到链接上，渲染层据此展开 1..n 个入口；
    /// 只有一处或未命中时清空（增量重解析后组可能缩小）。
    /// </summary>
    private static void ApplyGroup(MarkdownNode link, List<string> ids)
    {
        if (ids.Count < 2)
        {
            ClearGroup(link);
            return;
        }

        link.SetDerivedAttribute(
            "anchor-group-size",
            ids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        link.SetDerivedAttribute("anchor-group-ids", string.Join(' ', ids));
    }

    private static void ClearGroup(MarkdownNode link)
    {
        link.RemoveAttribute("anchor-group-size");
        link.RemoveAttribute("anchor-group-ids");
    }

    /// <summary>标题纯文本规范化：去首尾空白、空白折叠、ASCII 小写、标点转连字符、CJK 保留。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSeparator = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSeparator = builder.Length > 0;
                continue;
            }

            if (Rune.IsLetterOrDigit(rune))
            {
                if (pendingSeparator)
                {
                    builder.Append('-');
                    pendingSeparator = false;
                }

                builder.Append(Rune.ToLowerInvariant(rune));
                continue;
            }

            // 标点与符号统一折算成一个分隔连字符，连续出现不重复追加。
            pendingSeparator = builder.Length > 0;
        }

        return builder.ToString();
    }

    private static string TryDecode(string fragment)
    {
        if (fragment.IndexOf('%') < 0)
        {
            return fragment;
        }

        try
        {
            return Uri.UnescapeDataString(fragment);
        }
        catch (UriFormatException)
        {
            return fragment;
        }
    }
}
