using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.SyntaxTakeover;

/// <summary>
/// T1 的 <c>@@</c> 条目切分：把 <c>'''timeline</c> 围栏体按标记行切成条目切片。
/// 原实现写在 <c>MarkdigToNodeConverter.ScanTimelineItems</c>（转换器私有方法），
/// 现搬到 parser 侧：由 <c>TimelineBlock</c> 在首次读取时调用本类完成，
/// 转换器只消费切片、不再持有任何行扫描逻辑。
/// </summary>
public static class TimelineItemScanner
{
    /// <summary>
    /// 扫描围栏体。<paramref name="fenceStartOffset"/> 是开围栏的行首偏移，
    /// <paramref name="fenceStartLine"/> 是开围栏的 0-based 行号，
    /// <paramref name="closingFenceLineIndex"/> 是闭围栏的 0-based 行号（未闭合为 null）。
    /// 未闭合围栏扫描到 EOF；首个 @@ 之前的杂散文本不产生条目；
    /// 正文代码块内的行首 <c>@@ </c> 同样视为条目边界（与既有语法一致）。
    /// </summary>
    public static List<TimelineItemSlice> Scan(
        string source,
        int fenceStartOffset,
        int fenceStartLine,
        int? closingFenceLineIndex,
        string? itemMarker = null)
    {
        var slices = new List<TimelineItemSlice>();

        var pos = MoveToNextLine(source, fenceStartOffset);
        var lineIndex = fenceStartLine + 1;

        var markerStart = -1;
        var markerLine = -1;
        TimelineMarkerInfo? markerInfo = null;

        void Finish(int itemEnd, int lastBodyLineIndex)
        {
            if (markerInfo is null || markerStart < 0)
            {
                return;
            }

            // 条目原文去掉尾部换行；区间长度仍按原始行界计算。
            var rawEnd = itemEnd;
            while (rawEnd > markerStart && IsNewline(source[rawEnd - 1]))
            {
                rawEnd--;
            }

            var rawMarkdown = source.Substring(markerStart, rawEnd - markerStart);

            var bodyStart = 0;
            var bodyLength = 0;
            var bodyStartLine = markerLine + 2; // 1-based：标记行下一行为正文首行
            var markerNewline = source.IndexOf('\n', markerStart);
            if (markerNewline >= 0 && markerNewline + 1 < itemEnd)
            {
                bodyStart = markerNewline + 1;
                bodyLength = itemEnd - bodyStart;
                while (bodyLength > 0 && IsNewline(source[bodyStart + bodyLength - 1]))
                {
                    bodyLength--;
                }
            }

            var column = 1;
            for (var probe = markerStart; probe < rawEnd && source[probe] == ' '; probe++)
            {
                column++;
            }

            var range = new SourceRange
            {
                StartOffset = markerStart,
                Length = itemEnd - markerStart,
                StartLine = markerLine + 1,
                StartColumn = column,
                EndLine = lastBodyLineIndex + 1,
                EndColumn = 1
            };

            slices.Add(new TimelineItemSlice(
                markerInfo,
                range,
                rawMarkdown,
                bodyStart,
                bodyLength,
                bodyStartLine));
        }

        while (pos <= source.Length)
        {
            var newlineIndex = source.IndexOf('\n', pos);
            var lineEnd = newlineIndex < 0 ? source.Length : newlineIndex;
            var contentEnd = lineEnd > pos && source[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            var lineText = source.Substring(pos, contentEnd - pos);

            var isClosing = closingFenceLineIndex.HasValue
                && lineIndex == closingFenceLineIndex.Value;

            if (isClosing)
            {
                Finish(pos, lineIndex - 1);
                break;
            }

            if (TimelineMarker.IsMarkerLine(lineText, itemMarker))
            {
                // 上一条目正文止于本标记行行首。
                Finish(pos, lineIndex - 1);
                markerStart = pos;
                markerLine = lineIndex;
                markerInfo = TimelineMarker.TryParse(lineText, itemMarker);
            }

            if (newlineIndex < 0)
            {
                Finish(source.Length, lineIndex);
                break;
            }

            pos = newlineIndex + 1;
            lineIndex++;
        }

        return slices;
    }

    private static bool IsNewline(char ch) => ch is '\n' or '\r';

    // fromOffset 所在行的下一行行首偏移；无下一行时返回文本长度。
    private static int MoveToNextLine(string source, int fromOffset)
    {
        var newlineIndex = source.IndexOf('\n', fromOffset);
        return newlineIndex < 0 ? source.Length : newlineIndex + 1;
    }
}
