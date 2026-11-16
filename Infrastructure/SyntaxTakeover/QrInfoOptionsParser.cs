namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.SyntaxTakeover;

/// <summary>
/// 二维码围栏 info string 选项解析（<c>```qrcode ecc=H border=4</c>）。
/// 产物按源码中的出现顺序排列，转换器只按序落属性——属性先后顺序会影响渲染字节，不能重排。
/// </summary>
public static class QrInfoOptionsParser
{
    private static readonly char[] Separators = { ' ', '\t' };

    /// <summary>
    /// 解析 info 与参数（Markdig 把围栏 info 拆成 Info 首词与 Arguments 其余部分，需合并后再解析）。
    /// 校验口径与迁移前一致：<c>ecc/level</c> 仅接受大写 L/M/Q/H，<c>border/quiet/margin</c> 仅接受 0–10 的整数；
    /// 未知键与非法值一律**静默回落**（不写属性，渲染取缺省），不做拒绝、不产出诊断。
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Parse(string? info, string? arguments)
    {
        var result = new List<KeyValuePair<string, string>>();

        var fullInfo = string.IsNullOrWhiteSpace(arguments)
            ? info
            : info + " " + arguments;

        if (string.IsNullOrWhiteSpace(fullInfo))
        {
            return result;
        }

        var tokens = fullInfo.Trim().Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        // tokens[0] 是关键字本身（qrcode/qr），从其后解析。
        for (var i = 1; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var equalsIndex = token.IndexOf('=');
            if (equalsIndex <= 0 || equalsIndex >= token.Length - 1)
            {
                continue;
            }

            var key = token[..equalsIndex];
            var value = token[(equalsIndex + 1)..];

            if (key.Equals("ecc", StringComparison.OrdinalIgnoreCase)
                || key.Equals("level", StringComparison.OrdinalIgnoreCase))
            {
                if (value is "L" or "M" or "Q" or "H")
                {
                    result.Add(new KeyValuePair<string, string>("qr-ecc", value.ToUpperInvariant()));
                }
            }
            else if (key.Equals("border", StringComparison.OrdinalIgnoreCase)
                || key.Equals("quiet", StringComparison.OrdinalIgnoreCase)
                || key.Equals("margin", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out var modules) && modules is >= 0 and <= 10)
                {
                    result.Add(new KeyValuePair<string, string>("qr-border", modules.ToString()));
                }
            }
        }

        return result;
    }
}
