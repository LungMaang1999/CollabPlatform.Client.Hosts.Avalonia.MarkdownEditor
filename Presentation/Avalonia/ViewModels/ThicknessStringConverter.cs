using System;
using System.Globalization;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;

internal static class ThicknessStringConverter
{
    public static string? Format(ThicknessValue? value)
    {
        if (value is null) return null;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{value.Left},{value.Top},{value.Right},{value.Bottom}");
    }

    public static ThicknessValue? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        Span<double> values = stackalloc double[4];
        int count = 0;

        int start = 0;
        for (int i = 0; i <= span.Length; i++)
        {
            if (i == span.Length || span[i] == ',' || span[i] == ' ' || span[i] == ';')
            {
                if (i > start)
                {
                    ReadOnlySpan<char> token = span[start..i].Trim();
                    if (token.Length > 0)
                    {
                        if (count >= 4) return null;

                        if (token.EndsWith("px", StringComparison.OrdinalIgnoreCase) ||
                            token.EndsWith("pt", StringComparison.OrdinalIgnoreCase) ||
                            token.EndsWith("em", StringComparison.OrdinalIgnoreCase))
                        {
                            token = token[..^2].Trim();
                        }

                        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedVal))
                        {
                            values[count++] = parsedVal;
                        }
                        else
                        {
                            return null;
                        }
                    }
                }
                start = i + 1;
            }
        }

        return count switch
        {
            1 => new ThicknessValue(values[0]),
            2 => new ThicknessValue(values[0], values[1]),
            4 => new ThicknessValue(values[0], values[1], values[2], values[3]),
            _ => null
        };
    }
}
