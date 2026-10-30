using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;
using Xunit;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Tests;

/// <summary>
/// 验证 FenceStateScanner 的未闭合围栏检测。
/// </summary>
public class FenceStateScannerTests
{
    [Fact]
    public void FindTrailingUnclosedFenceStartLine_AllClosed_ReturnsNull()
    {
        var source = "```\ncode\n```\n";
        Assert.Null(FenceStateScanner.FindTrailingUnclosedFenceStartLine(source));
    }

    [Fact]
    public void FindTrailingUnclosedFenceStartLine_UnclosedBacktickFence_ReturnsStartLine()
    {
        var source = "line1\n```\nunclosed code\n";
        Assert.Equal(2, FenceStateScanner.FindTrailingUnclosedFenceStartLine(source));
    }

    [Fact]
    public void FindTrailingUnclosedFenceStartLine_UnclosedTildeFence_ReturnsStartLine()
    {
        var source = "~~~\ncode\n";
        Assert.Equal(1, FenceStateScanner.FindTrailingUnclosedFenceStartLine(source));
    }

    [Fact]
    public void FindTrailingUnclosedFenceStartLine_SecondFenceUnclosed_ReturnsSecondStart()
    {
        var source = "```\nfirst\n```\ntext\n~~~\nunclosed\n";
        Assert.Equal(5, FenceStateScanner.FindTrailingUnclosedFenceStartLine(source));
    }

    [Fact]
    public void FindTrailingUnclosedFenceStartLine_CustomFenceUnclosed_ReturnsStartLine()
    {
        // 自定义围栏（''' 开头 + 关键字）由 IsCustomFenceKeyword 判定。
        var source = "'''poem\ncontent\n";
        Assert.Equal(1, FenceStateScanner.FindTrailingUnclosedFenceStartLine(source));
    }

    [Fact]
    public void FindTrailingUnclosedFenceStartLine_CustomFenceAllKeywords_Recognized()
    {
        string[] keywords = { "ancient-poetry", "ancient-poem", "poetry", "poem", "verse", "shi", "ci", "qu", "image", "img", "timeline" };
        foreach (var kw in keywords)
        {
            var source = $"'''{kw}\nbody\n";
            var line = FenceStateScanner.FindTrailingUnclosedFenceStartLine(source);
            Assert.NotNull(line);
            Assert.Equal(1, line.Value);
        }
    }

    [Fact]
    public void FindTrailingUnclosedFenceStartLine_NonKeywordTripleQuote_NotTreatedAsFence()
    {
        // ''' 后接非关键字不应视为自定义围栏，整段没有围栏 → null。
        var source = "'''notakind\nbody\n";
        Assert.Null(FenceStateScanner.FindTrailingUnclosedFenceStartLine(source));
    }

    [Fact]
    public void FindTrailingUnclosedFenceStartLine_KeywordWithTrailingSpace_Recognized()
    {
        var source = "'''poem extra info\nbody\n";
        Assert.Equal(1, FenceStateScanner.FindTrailingUnclosedFenceStartLine(source));
    }

    [Fact]
    public void FindTrailingUnclosedFenceStartLine_EmptySource_ReturnsNull()
    {
        Assert.Null(FenceStateScanner.FindTrailingUnclosedFenceStartLine(string.Empty));
    }

    [Fact]
    public void FindTrailingUnclosedFenceStartLine_FenceClosedWithFewerChars_RemainsOpen()
    {
        // 开启围栏是 ```（3 个），闭合只有 ``（2 个）不构成闭合 → 仍未闭合。
        var source = "```\ncode\n``\n";
        Assert.Equal(1, FenceStateScanner.FindTrailingUnclosedFenceStartLine(source));
    }
}
