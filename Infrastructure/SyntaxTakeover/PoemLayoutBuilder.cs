using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.SyntaxTakeover;

/// <summary>
/// 诗体围栏体切分：元数据/正文以首个单独 <c>---</c> 行为界，正文按空行分组为诗句组，
/// 元数据按 <c>key: value</c> 逐行读取。
/// 原实现写在转换器的三个私有方法里（部件展开 / 段切分 / 元数据读取），现搬到 parser 侧：由 <c>PoemBlock</c> 首次读取时调用，
/// 转换器只按 <see cref="PoemLayout"/> 建节点。
/// </summary>
public static class PoemLayoutBuilder
{
    public static PoemLayout Build(string bodyText, SourceRange partRange)
    {
        var normalized = bodyText.Replace("\r\n", "\n");

        SplitSections(normalized, out var metadataText, out var contentText);
        var metadata = ParseMetadata(metadataText);

        var header = new List<PoemFieldSlice>();
        AddField(header, PoemFieldKind.Title, Lookup(metadata, "title"), partRange);
        AddField(header, PoemFieldKind.Author, Lookup(metadata, "author"), partRange);
        AddField(header, PoemFieldKind.Dynasty, Lookup(metadata, "dynasty"), partRange);
        AddField(header, PoemFieldKind.Form, Lookup(metadata, "form"), partRange);

        var stanzas = new List<PoemStanzaSlice>();
        List<PoemLineSlice>? current = null;
        foreach (var raw in contentText.Split('\n'))
        {
            if (raw.Length == 0)
            {
                current = null;
                continue;
            }

            if (current is null)
            {
                current = [];
                stanzas.Add(new PoemStanzaSlice(current!, partRange));
            }

            current.Add(new PoemLineSlice(raw, partRange));
        }

        var footer = new List<PoemFieldSlice>();
        AddField(footer, PoemFieldKind.Source, Lookup(metadata, "source"), partRange);
        AddField(footer, PoemFieldKind.Note, Lookup(metadata, "note"), partRange);

        return new PoemLayout(header, stanzas, footer, partRange);
    }

    private static void AddField(
        ICollection<PoemFieldSlice> target,
        PoemFieldKind kind,
        string? value,
        SourceRange range)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target.Add(new PoemFieldSlice(kind, value!, range));
        }
    }

    private static string? Lookup(Dictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) ? value : null;

    /// <summary>元数据/正文切分：以首个单独的 "---" 行为界（与预览渲染一致）。</summary>
    private static void SplitSections(string normalized, out string metadataText, out string bodyText)
    {
        metadataText = string.Empty;
        bodyText = string.Empty;

        if (normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            bodyText = normalized.Substring(4);
            return;
        }

        if (normalized == "---")
        {
            return;
        }

        var separatorIndex = normalized.IndexOf("\n---\n", StringComparison.Ordinal);
        if (separatorIndex >= 0)
        {
            metadataText = normalized.Substring(0, separatorIndex);
            bodyText = normalized.Substring(separatorIndex + 5);
            return;
        }

        separatorIndex = normalized.IndexOf("\n---", StringComparison.Ordinal);
        if (separatorIndex >= 0)
        {
            metadataText = normalized.Substring(0, separatorIndex);
            bodyText = separatorIndex + 4 < normalized.Length
                ? normalized.Substring(separatorIndex + 4)
                : string.Empty;
            return;
        }

        bodyText = normalized;
    }

    private static Dictionary<string, string> ParseMetadata(string metadataText)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(metadataText))
        {
            return result;
        }

        foreach (var raw in metadataText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0)
            {
                continue;
            }

            var key = line.Substring(0, colonIndex).Trim();
            var value = line.Substring(colonIndex + 1).Trim();
            if (key.Length > 0)
            {
                result[key] = value;
            }
        }

        return result;
    }
}
