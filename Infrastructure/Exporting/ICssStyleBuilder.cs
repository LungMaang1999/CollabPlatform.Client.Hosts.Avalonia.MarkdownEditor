using System;
using System.Globalization;
using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Exporting.Html;

public interface ICssStyleBuilder
{
    string Build(ComputedStyle? style);

    string Build(NodeStyle? style);
}

public sealed class CssStyleBuilder : ICssStyleBuilder
{
    public string Build(ComputedStyle? style)
    {
        if (style is null) return string.Empty;

        return BuildCore(
            fontFamily: style.FontFamily,
            fontSize: style.FontSize,
            foregroundColor: style.ForegroundColor,
            backgroundColor: style.BackgroundColor,
            bold: style.Bold,
            italic: style.Italic,
            lineHeight: style.LineHeight,
            textAlign: style.TextAlign,
            verticalAlign: style.VerticalAlign,
            borderWidth: style.BorderWidth,
            borderColor: style.BorderColor,
            borderRadius: style.BorderRadius,
            width: style.Width,
            marginTop: style.Margin?.Top,
            marginRight: style.Margin?.Right,
            marginBottom: style.Margin?.Bottom,
            marginLeft: style.Margin?.Left,
            paddingTop: style.Padding?.Top,
            paddingRight: style.Padding?.Right,
            paddingBottom: style.Padding?.Bottom,
            paddingLeft: style.Padding?.Left,
            customCss: style.CustomCss
        );
    }

    public string Build(NodeStyle? style)
    {
        if (style is null) return string.Empty;

        return BuildCore(
            fontFamily: style.FontFamily,
            fontSize: style.FontSize,
            foregroundColor: style.ForegroundColor,
            backgroundColor: style.BackgroundColor,
            bold: style.Bold,
            italic: style.Italic,
            lineHeight: style.LineHeight,
            textAlign: style.TextAlign,
            verticalAlign: style.VerticalAlign,
            borderWidth: style.BorderWidth,
            borderColor: style.BorderColor,
            borderRadius: style.BorderRadius,
            width: style.Width,
            marginTop: style.Margin?.Top,
            marginRight: style.Margin?.Right,
            marginBottom: style.Margin?.Bottom,
            marginLeft: style.Margin?.Left,
            paddingTop: style.Padding?.Top,
            paddingRight: style.Padding?.Right,
            paddingBottom: style.Padding?.Bottom,
            paddingLeft: style.Padding?.Left,
            customCss: style.CustomCss
        );
    }

    private static string BuildCore(
        string? fontFamily,
        double? fontSize,
        string? foregroundColor,
        string? backgroundColor,
        bool? bold,
        bool? italic,
        double? lineHeight,
        string? textAlign,
        string? verticalAlign,
        double? borderWidth,
        string? borderColor,
        double? borderRadius,
        double? width,
        double? marginTop,
        double? marginRight,
        double? marginBottom,
        double? marginLeft,
        double? paddingTop,
        double? paddingRight,
        double? paddingBottom,
        double? paddingLeft,
        string? customCss)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(fontFamily))
        {
            sb.Append("font-family: ").Append(fontFamily).Append("; ");
        }

        if (fontSize.HasValue && fontSize.Value > 0)
        {
            sb.Append("font-size: ").Append(fontSize.Value.ToString("0.##", CultureInfo.InvariantCulture)).Append("px; ");
        }

        if (!string.IsNullOrWhiteSpace(foregroundColor))
        {
            // 只输出 --node-color CSS 变量，不输出 inline color: 声明。
            // 宿主 CSS 统一通过 var(--node-color, 语义默认色) 消费颜色：
            //   pre { color: var(--node-color, var(--color-code-text)) }
            //   a   { color: var(--node-color, var(--color-primary)) }
            // 这样做的关键收益是：Prism token span（.token.keyword { color: #07a }）
            // 的类选择器特异性（0,0,2,0）高于宿主 pre 的类选择器（0,0,0,1），
            // 语法着色能正常生效；如果这里再写 inline color:（特异性 1,0,0,0），
            // 会把 Prism token 颜色全部压掉，只剩父元素单一文字色。
            // 古诗等"叶子块"内部 HTML 无独立 AST 节点，同样靠 var(--node-color) 取色。
            sb.Append("--node-color: ").Append(foregroundColor).Append("; ");
        }

        if (!string.IsNullOrWhiteSpace(backgroundColor))
        {
            // 同步输出 --node-bg：mark 等带渐变背景的元素通过它在"用户设底色"
            // 时收敛为纯色；未设时保留各自的默认渐变。
            sb.Append("--node-bg: ").Append(backgroundColor).Append("; ");
            sb.Append("background-color: ").Append(backgroundColor).Append("; ");
        }

        if (bold == true)
        {
            sb.Append("font-weight: bold; ");
        }
        else if (bold == false)
        {
            // 三态中的"强制不加粗"：对标题等本身加粗的祖先也有实际效果。
            sb.Append("font-weight: normal; ");
        }

        if (italic == true)
        {
            sb.Append("font-style: italic; ");
        }
        else if (italic == false)
        {
            sb.Append("font-style: normal; ");
        }

        if (lineHeight.HasValue && lineHeight.Value > 0)
        {
            sb.Append("line-height: ").Append(lineHeight.Value.ToString("0.##", CultureInfo.InvariantCulture)).Append("; ");
        }

        if (!string.IsNullOrWhiteSpace(textAlign))
        {
            sb.Append("text-align: ").Append(textAlign.ToLowerInvariant()).Append("; ");
        }

        if (!string.IsNullOrWhiteSpace(verticalAlign))
        {
            sb.Append("vertical-align: ").Append(NormalizeVerticalAlign(verticalAlign)).Append("; ");
        }

        if ((borderWidth.HasValue && borderWidth.Value > 0) || !string.IsNullOrWhiteSpace(borderColor))
        {
            var bw = (borderWidth ?? 1).ToString("0.##", CultureInfo.InvariantCulture);
            var bc = string.IsNullOrWhiteSpace(borderColor) ? "#000000" : borderColor;
            // 拆成长手属性：自定义 CSS 中仍可单独覆写某一条边（如 border-left-color），
            // 简写会把后续单边声明在同优先级下顶掉。
            sb.Append("border-style: solid; ");
            sb.Append("border-width: ").Append(bw).Append("px; ");
            sb.Append("border-color: ").Append(bc).Append("; ");
        }

        if (borderRadius.HasValue && borderRadius.Value > 0)
        {
            sb.Append("border-radius: ")
              .Append(borderRadius.Value.ToString("0.##", CultureInfo.InvariantCulture))
              .Append("px; ");
        }

        if (width.HasValue && width.Value > 0)
        {
            sb.Append("width: ")
              .Append(width.Value.ToString("0.##", CultureInfo.InvariantCulture))
              .Append("px; ");
        }

        if (marginTop.HasValue || marginRight.HasValue || marginBottom.HasValue || marginLeft.HasValue)
        {
            sb.Append("margin: ")
              .Append((marginTop ?? 0).ToString("0.##", CultureInfo.InvariantCulture)).Append("px ")
              .Append((marginRight ?? 0).ToString("0.##", CultureInfo.InvariantCulture)).Append("px ")
              .Append((marginBottom ?? 0).ToString("0.##", CultureInfo.InvariantCulture)).Append("px ")
              .Append((marginLeft ?? 0).ToString("0.##", CultureInfo.InvariantCulture)).Append("px; ");
        }

        if (paddingTop.HasValue || paddingRight.HasValue || paddingBottom.HasValue || paddingLeft.HasValue)
        {
            sb.Append("padding: ")
              .Append((paddingTop ?? 0).ToString("0.##", CultureInfo.InvariantCulture)).Append("px ")
              .Append((paddingRight ?? 0).ToString("0.##", CultureInfo.InvariantCulture)).Append("px ")
              .Append((paddingBottom ?? 0).ToString("0.##", CultureInfo.InvariantCulture)).Append("px ")
              .Append((paddingLeft ?? 0).ToString("0.##", CultureInfo.InvariantCulture)).Append("px; ");
        }

        if (!string.IsNullOrWhiteSpace(customCss))
        {
            var trimmedCss = customCss.Trim();
            sb.Append(trimmedCss);
            if (!trimmedCss.EndsWith(';'))
            {
                sb.Append(';');
            }
            sb.Append(' ');
        }

        return sb.ToString().Trim();
    }

    private static string NormalizeVerticalAlign(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "top" => "top",
            "middle" or "center" => "middle",
            "bottom" => "bottom",
            _ => "middle",
        };
    }
}
