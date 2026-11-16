using System.Globalization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// 用户块属性声明的取值规则。加载期（校验默认值）与解析期（校验实例值）
/// 必须用同一套判定，避免“定义里合法、实例里非法”的口径分叉。
/// </summary>
public static class UserBlockAttributeRules
{
    /// <summary>
    /// 值是否满足声明约束。<see cref="UserBlockAttributeKind.Enum"/> 未声明允许值时
    /// 不做取值限制（加载期会单独告警），与“按 Text 处理”一致。
    /// </summary>
    public static bool IsValidValue(
        UserBlockAttributeKind kind,
        string value,
        IReadOnlyList<string> allowedValues)
    {
        switch (kind)
        {
            case UserBlockAttributeKind.Number:
                return double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out _);

            case UserBlockAttributeKind.Boolean:
                return bool.TryParse(value, out _);

            case UserBlockAttributeKind.Enum:
                if (allowedValues.Count == 0)
                {
                    return true;
                }

                return allowedValues.Contains(value, StringComparer.OrdinalIgnoreCase);

            case UserBlockAttributeKind.Url:
                return IsValidUrl(value);

            default:
                return true;
        }
    }

    /// <summary>把声明的约束描述成可读短语，供诊断信息复用。</summary>
    public static string DescribeConstraint(
        UserBlockAttributeKind kind,
        IReadOnlyList<string> allowedValues) => kind switch
    {
        UserBlockAttributeKind.Number => "需为数字",
        UserBlockAttributeKind.Boolean => "需为 true/false",
        UserBlockAttributeKind.Enum => allowedValues.Count == 0
            ? "未声明允许值，按任意文本处理"
            : "允许值：" + string.Join("、", allowedValues),
        UserBlockAttributeKind.Url => "需为绝对 URL 或以 /、# 开头的站内地址",
        _ => "任意文本"
    };

    private static bool IsValidUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value[0] is '/' or '#')
        {
            return true;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Scheme);
    }
}
