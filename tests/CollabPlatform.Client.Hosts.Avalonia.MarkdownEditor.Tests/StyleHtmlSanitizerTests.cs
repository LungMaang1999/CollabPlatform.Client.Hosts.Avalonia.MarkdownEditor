using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Strategies;
using Xunit;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Tests;

/// <summary>
/// 验证 StyleHtmlSanitizer.SanitizeStyle 的清洗行为。
/// </summary>
public class StyleHtmlSanitizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizeStyle_NullOrWhitespace_ReturnsEmpty(string? css)
    {
        Assert.Equal(string.Empty, StyleHtmlSanitizer.SanitizeStyle(css));
    }

    [Fact]
    public void SanitizeStyle_NormalCss_Preserved()
    {
        var css = "color: red; font-size: 14px;";
        Assert.Equal(css, StyleHtmlSanitizer.SanitizeStyle(css));
    }

    [Fact]
    public void SanitizeStyle_DoubleQuotes_ReplacedWithSingleQuotes()
    {
        // 双引号会破坏 style=\"...\" 属性边界，必须替换为单引号。
        var result = StyleHtmlSanitizer.SanitizeStyle("font-family: \"Arial\";");
        Assert.DoesNotContain("\"", result);
        Assert.Contains("'", result);
    }

    [Theory]
    [InlineData("color: red; javascript:alert(1);", "javascript:")]
    [InlineData("JAVAscript:alert(1)", "javascript:")]
    [InlineData("color: red; expression(alert(1));", "expression(")]
    [InlineData("EXPRESSION(alert(1))", "expression(")]
    public void SanitizeStyle_UnsafeTokens_Removed(string css, string unsafeToken)
    {
        var result = StyleHtmlSanitizer.SanitizeStyle(css);
        Assert.DoesNotContain(unsafeToken, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeStyle_MixedUnsafeTokens_AllRemovedAndSafePartsKept()
    {
        var css = "color: blue; \"quote\"; javascript:x; expression(y);";
        var result = StyleHtmlSanitizer.SanitizeStyle(css);

        Assert.Contains("color: blue", result);
        Assert.DoesNotContain("\"", result);
        Assert.DoesNotContain("javascript", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expression", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeStyle_RepeatedCalls_ProduceSameResult()
    {
        // 静态编译正则应保证幂等且无状态副作用。
        var css = "color: red; javascript:bad;";
        var first = StyleHtmlSanitizer.SanitizeStyle(css);
        var second = StyleHtmlSanitizer.SanitizeStyle(css);
        Assert.Equal(first, second);
    }
}
