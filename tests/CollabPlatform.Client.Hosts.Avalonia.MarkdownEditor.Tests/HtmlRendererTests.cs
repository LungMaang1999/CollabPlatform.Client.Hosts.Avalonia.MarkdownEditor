using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Exporting.Html;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;
using Xunit;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Tests;

/// <summary>
/// 验证 HtmlRenderer 对诗块与时间线的渲染输出。
/// </summary>
public class HtmlRendererTests
{
    private static MarkdownParser CreateParser() => new(new MarkdownParserOptions());

    private static HtmlRenderer CreateRenderer(MarkdownDocument document)
    {
        IStyleResolver resolver = new StyleResolver(document);
        ICssStyleBuilder cssBuilder = new CssStyleBuilder();
        return new HtmlRenderer(resolver, cssBuilder);
    }

    [Fact]
    public void Render_PoemBlock_ProducesExpectedSections()
    {
        var source = "'''poem\n静夜思\n李白〔唐〕\n床前明月光，\n疑是地上霜。\n'''\n";
        var parser = CreateParser();
        var document = parser.Parse(source).Document;
        var renderer = CreateRenderer(document);

        var html = renderer.Render(document).Html;

        // 诗块容器必须存在。
        Assert.Contains("poem-block", html);
        // 标题、作者、正文行应出现在输出中。
        Assert.Contains("静夜思", html);
        Assert.Contains("李白", html);
        Assert.Contains("床前明月光", html);
        Assert.Contains("疑是地上霜", html);
    }

    [Fact]
    public void Render_PoemBlockWithMultipleStanzas_RendersAllStanzas()
    {
        var source = "'''poem\n标题\n作者\n第一行。\n第二行。\n\n第三行。\n第四行。\n'''\n";
        var parser = CreateParser();
        var document = parser.Parse(source).Document;
        var renderer = CreateRenderer(document);

        var html = renderer.Render(document).Html;

        // 空行分节：每节正文行都应渲染。
        Assert.Contains("第一行", html);
        Assert.Contains("第二行", html);
        Assert.Contains("第三行", html);
        Assert.Contains("第四行", html);
    }

    [Fact]
    public void Render_TimelineWithItems_RendersAllItems()
    {
        var source = "'''timeline\n@@ 2024\n事件一\n\n@@ 2025\n事件二\n'''\n";
        var parser = CreateParser();
        var document = parser.Parse(source).Document;
        var renderer = CreateRenderer(document);

        var html = renderer.Render(document).Html;

        Assert.Contains("md-timeline", html);
        Assert.Contains("2024", html);
        Assert.Contains("事件一", html);
        Assert.Contains("2025", html);
        Assert.Contains("事件二", html);
    }

    [Fact]
    public void Render_TimelineEmpty_RendersEmptyPlaceholder()
    {
        var source = "'''timeline\n'''\n";
        var parser = CreateParser();
        var document = parser.Parse(source).Document;
        var renderer = CreateRenderer(document);

        var html = renderer.Render(document).Html;

        // 空时间线应输出占位提示而非崩溃。
        Assert.Contains("md-timeline", html);
        Assert.Contains("md-tl-empty", html);
    }

    [Fact]
    public void Render_PoemAndTimelineInSameDocument_BothRendered()
    {
        var source =
            "'''poem\n诗\n作者\n一行。\n'''\n\n" +
            "'''timeline\n@@ 年\n事\n'''\n";
        var parser = CreateParser();
        var document = parser.Parse(source).Document;
        var renderer = CreateRenderer(document);

        var html = renderer.Render(document).Html;

        Assert.Contains("poem-block", html);
        Assert.Contains("md-timeline", html);
        Assert.Contains("一行", html);
        Assert.Contains("事", html);
    }
}
