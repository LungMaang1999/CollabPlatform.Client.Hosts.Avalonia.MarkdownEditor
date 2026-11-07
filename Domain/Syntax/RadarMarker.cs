using System.Globalization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>雷达图开围栏选项的解析结果。</summary>
/// <param name="Title">图表标题，空字符串表示无标题。</param>
/// <param name="ShowLabels">是否在数据点旁标注数值。</param>
/// <param name="MaxScale">数值量程上限；null 表示按数据最大值自适应。</param>
/// <param name="ShowLegend">是否显示底部图例。</param>
/// <param name="FillOpacity">多边形填充不透明度（0–100）。</param>
/// <param name="Size">图表尺寸：small / medium / large。</param>
public sealed record RadarFenceInfo(
    string Title,
    bool ShowLabels,
    double? MaxScale,
    bool ShowLegend,
    int FillOpacity,
    string Size);

/// <summary>雷达图单个轴（维度）的数据。</summary>
/// <param name="Label">轴标签。</param>
/// <param name="Values">该轴上各系列的数值（顺序与系列名一致）。</param>
public sealed record RadarAxis(string Label, IReadOnlyList<double> Values);

/// <summary>雷达图表格数据解析结果。</summary>
/// <param name="SeriesNames">系列名列表。</param>
/// <param name="Axes">轴列表，每个轴含各系列数值。</param>
public sealed record RadarTable(
    IReadOnlyList<string> SeriesNames,
    IReadOnlyList<RadarAxis> Axes);

/// <summary>
/// 雷达图开围栏选项与表格数据的解析，供转换器与渲染器共用。
/// </summary>
public static class RadarMarker
{
    /// <summary>填充不透明度缺省值（%）。</summary>
    public const int DefaultFillOpacity = 30;

    /// <summary>填充不透明度允许范围。</summary>
    public const int MinFillOpacity = 0;
    public const int MaxFillOpacity = 100;

    /// <summary>图表尺寸选项。</summary>
    public const string SizeSmall = "small";
    public const string SizeMedium = "medium";
    public const string SizeLarge = "large";

    /// <summary>各尺寸对应的 SVG 参数：viewBox 边长、圆心、绘图半径。</summary>
    public static (int ViewBox, double Cx, double Cy, double Radius) SizeGeometry(string size) => size switch
    {
        SizeSmall => (360, 180.0, 180.0, 110.0),
        SizeLarge => (480, 240.0, 240.0, 150.0),
        _ => (420, 210.0, 210.0, 130.0),
    };

    /// <summary>
    /// 解析开围栏 <c>radar</c>/<c>spider</c> 之后的选项串：
    /// <c>title=文本</c>、<c>labels=show|hide</c>、<c>scale=auto|max:N</c>、
    /// <c>legend=show|hide</c>、<c>fill=0..100</c>、<c>size=small|medium|large</c>。
    /// 未知选项忽略，非法值回退缺省。
    /// </summary>
    public static RadarFenceInfo ParseFenceInfo(string? info)
    {
        var title = string.Empty;
        var showLabels = false;
        double? maxScale = null;
        var showLegend = true;
        var fillOpacity = DefaultFillOpacity;
        var size = SizeMedium;

        if (!string.IsNullOrWhiteSpace(info))
        {
            foreach (var token in info.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = token.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var key = token[..eq].Trim().ToLowerInvariant();
                var val = token[(eq + 1)..].Trim();

                switch (key)
                {
                    case "title":
                        title = val.Trim();
                        break;
                    case "labels":
                        showLabels = val.Equals("show", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "scale":
                        {
                            var v = val.ToLowerInvariant();
                            if (v.StartsWith("max:", StringComparison.Ordinal)
                                && double.TryParse(v[4..], NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out var explicitMax)
                                && explicitMax > 0)
                            {
                                maxScale = explicitMax;
                            }
                            // "auto" 或非法值均保持 null（自适应）。
                            break;
                        }
                    case "legend":
                        showLegend = !val.Equals("hide", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "fill" when int.TryParse(val, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var fill)
                        && fill is >= MinFillOpacity and <= MaxFillOpacity:
                        fillOpacity = fill;
                        break;
                    case "size":
                        {
                            var s = val.ToLowerInvariant();
                            size = s switch
                            {
                                SizeSmall => SizeSmall,
                                SizeLarge => SizeLarge,
                                _ => SizeMedium,
                            };
                            break;
                        }
                }
            }
        }

        return new RadarFenceInfo(title, showLabels, maxScale, showLegend, fillOpacity, size);
    }

    /// <summary>
    /// 解析围栏内的类 Markdown 表格：首行系列名（首列通常为"维度"占位），
    /// 后续每行首列为轴名、其余为该轴各系列的数值。空行与分隔行（|---|）忽略。
    /// </summary>
    public static RadarTable? ParseTable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var rows = new List<string[]>();
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // 去掉首尾的 | 再分割，避免空首尾单元格。
            var trimmed = line.Trim('|');
            var cells = trimmed.Split('|', StringSplitOptions.None)
                .Select(c => c.Trim())
                .ToArray();

            // 跳过分隔行：所有单元格只含 - : . 空白
            if (cells.Length > 0 && cells.All(IsSeparatorCell))
            {
                continue;
            }

            rows.Add(cells);
        }

        if (rows.Count == 0)
        {
            return null;
        }

        var header = rows[0];
        if (header.Length < 2)
        {
            return null;
        }

        // 首行首列为角标占位（如"维度"），后续为系列名。
        var seriesNames = header.Skip(1)
            .Where(s => s.Length > 0)
            .ToList();
        if (seriesNames.Count == 0)
        {
            return null;
        }

        var axes = new List<RadarAxis>();
        for (var r = 1; r < rows.Count; r++)
        {
            var cells = rows[r];
            if (cells.Length == 0)
            {
                continue;
            }

            var label = cells[0];
            if (label.Length == 0)
            {
                continue;
            }

            var values = new double[seriesNames.Count];
            for (var c = 0; c < seriesNames.Count; c++)
            {
                var cellIndex = c + 1;
                values[c] = cellIndex < cells.Length
                    && double.TryParse(cells[cellIndex], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var v)
                        ? v
                        : 0;
            }

            axes.Add(new RadarAxis(label, values));
        }

        if (axes.Count == 0)
        {
            return null;
        }

        return new RadarTable(seriesNames, axes);
    }

    private static bool IsSeparatorCell(string cell) =>
        cell.Length > 0 && cell.All(ch => ch is '-' or ':' or '.' or ' ' or '\t');

    /// <summary>
    /// 根据解析结果重新生成开围栏选项串。只输出非缺省选项，保持源码简洁。
    /// </summary>
    public static string BuildFenceInfo(RadarFenceInfo info)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(info.Title))
            parts.Add("title=" + info.Title.Trim());

        if (info.ShowLabels)
            parts.Add("labels=show");

        if (info.MaxScale.HasValue)
            parts.Add("scale=max:" + info.MaxScale.Value.ToString("G", CultureInfo.InvariantCulture));

        if (!info.ShowLegend)
            parts.Add("legend=hide");

        if (info.FillOpacity != DefaultFillOpacity)
            parts.Add("fill=" + info.FillOpacity.ToString(CultureInfo.InvariantCulture));

        if (!string.Equals(info.Size, SizeMedium, StringComparison.Ordinal))
            parts.Add("size=" + info.Size);

        return parts.Count == 0 ? string.Empty : " " + string.Join(" ", parts);
    }
}
