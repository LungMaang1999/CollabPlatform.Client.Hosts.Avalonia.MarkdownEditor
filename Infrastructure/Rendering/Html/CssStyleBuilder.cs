using System.Globalization;
using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering;

/// <summary>
/// 内部 CSS 样式生成器，对齐持久化 DTO 模型
/// </summary>
internal sealed class CssStyleBuilder
{
    public string Build(NodeStyleDto? style)
    {
        if (style is null) return string.Empty;

        var sb = new StringBuilder(128);

        Append(sb, "font-family", string.IsNullOrWhiteSpace(style.FontFamily) ? null : $"'{style.FontFamily}'");
        Append(sb, "font-size", style.FontSize, "pt");
        Append(sb, "color", style.ForegroundColor);
        Append(sb, "background-color", style.BackgroundColor);

        if (style.Bold.HasValue)
            Append(sb, "font-weight", style.Bold.Value ? "bold" : "normal");

        if (style.Italic.HasValue)
            Append(sb, "font-style", style.Italic.Value ? "italic" : "normal");

        Append(sb, "line-height", style.LineHeight);
        Append(sb, "text-align", style.TextAlign);

        // 边框逻辑：只有当颜色和边框宽度同时有效时生成
        if (!string.IsNullOrWhiteSpace(style.BorderColor) && style.BorderWidth is > 0)
        {
            var widthStr = style.BorderWidth.Value.ToString(CultureInfo.InvariantCulture);
            Append(sb, "border", $"{widthStr}px solid {style.BorderColor}");
        }

        // 外边距与内边距
        AppendThickness(sb, "margin", style.Margin);
        AppendThickness(sb, "padding", style.Padding);

        // 自定义 CSS 拼接
        if (!string.IsNullOrWhiteSpace(style.CustomCss))
        {
            var trimmed = style.CustomCss.Trim();
            sb.Append(trimmed);
            if (!trimmed.EndsWith(';'))
            {
                sb.Append(';');
            }
            sb.Append(' ');
        }

        return sb.ToString().TrimEnd();
    }

    #region 私有辅助函数

    private static void AppendThickness(StringBuilder css, string name, ThicknessValueDto? thickness)
    {
        if (thickness is null) return;

        // 如果上下左右全为 0，跳过输出
        if (thickness.Left == 0 && thickness.Top == 0 && thickness.Right == 0 && thickness.Bottom == 0)
            return;

        var left = thickness.Left.ToString(CultureInfo.InvariantCulture);
        var top = thickness.Top.ToString(CultureInfo.InvariantCulture);
        var right = thickness.Right.ToString(CultureInfo.InvariantCulture);
        var bottom = thickness.Bottom.ToString(CultureInfo.InvariantCulture);

        css.Append(CultureInfo.InvariantCulture, $"{name}: {top}px {right}px {bottom}px {left}px; ");
    }

    private static void Append(StringBuilder css, string name, double? value, string unit = "")
    {
        if (!value.HasValue || value.Value <= 0) return;
        css.Append(CultureInfo.InvariantCulture, $"{name}: {value.Value}{unit}; ");
    }

    private static void Append(StringBuilder css, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        css.Append(CultureInfo.InvariantCulture, $"{name}: {value}; ");
    }

    #endregion
}