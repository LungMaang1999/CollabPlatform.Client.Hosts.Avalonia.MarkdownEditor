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
            borderWidth: style.BorderWidth,
            borderColor: style.BorderColor,
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
            borderWidth: style.BorderWidth,
            borderColor: style.BorderColor,
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
        double? borderWidth,
        string? borderColor,
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
            sb.Append("color: ").Append(foregroundColor).Append("; ");
        }

        if (!string.IsNullOrWhiteSpace(backgroundColor))
        {
            sb.Append("background-color: ").Append(backgroundColor).Append("; ");
        }

        if (bold == true)
        {
            sb.Append("font-weight: bold; ");
        }

        if (italic == true)
        {
            sb.Append("font-style: italic; ");
        }

        if (lineHeight.HasValue && lineHeight.Value > 0)
        {
            sb.Append("line-height: ").Append(lineHeight.Value.ToString("0.##", CultureInfo.InvariantCulture)).Append("; ");
        }

        if (!string.IsNullOrWhiteSpace(textAlign))
        {
            sb.Append("text-align: ").Append(textAlign.ToLowerInvariant()).Append("; ");
        }

        if ((borderWidth.HasValue && borderWidth.Value > 0) || !string.IsNullOrWhiteSpace(borderColor))
        {
            var bw = (borderWidth ?? 1).ToString("0.##", CultureInfo.InvariantCulture);
            var bc = string.IsNullOrWhiteSpace(borderColor) ? "#000000" : borderColor;
            sb.Append("border: ").Append(bw).Append("px solid ").Append(bc).Append("; ");
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
}
