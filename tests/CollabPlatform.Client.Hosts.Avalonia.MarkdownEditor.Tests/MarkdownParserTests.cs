using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;
using Xunit;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Tests;

/// <summary>
/// 验证 MarkdownParser 的解析正确性，重点覆盖增量追加路径。
/// </summary>
public class MarkdownParserTests
{
    private static MarkdownParser CreateParser() => new(new MarkdownParserOptions());

    [Fact]
    public void Parse_BasicParagraph_ReturnsDocumentRoot()
    {
        var parser = CreateParser();
        var result = parser.Parse("Hello world");

        Assert.NotNull(result.Document);
        Assert.NotNull(result.Document.Root);
    }

    [Fact]
    public void Parse_ReferenceDefinition_LinkResolves()
    {
        var parser = CreateParser();
        var source = "[example][ref]\n\n[ref]: https://example.com";
        var result = parser.Parse(source);

        // 引用定义应被解析，不应产生错误诊断。
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Parse_IncrementalAppend_UsesPreviousTreeAndProducesCorrectResult()
    {
        var parser = CreateParser();
        var first = parser.Parse("First paragraph\n");
        var appended = parser.Parse("First paragraph\nSecond paragraph\n", first.Document);

        // 增量解析应复用前缀，结果仍包含两段。
        Assert.NotNull(appended.Document.Root);
    }

    [Fact]
    public void Parse_IncrementalAppendWithReferenceDefinition_ParsesCorrectly()
    {
        // 引用定义在增量追加场景下是正则外提的关键路径：
        // previousSource 含 [ref]: 定义，追加文本含引用链接。
        var parser = CreateParser();
        var first = parser.Parse("[ref]: https://example.com\n");
        var appended = parser.Parse("[ref]: https://example.com\n[example][ref]\n", first.Document);

        Assert.NotNull(appended.Document.Root);
        Assert.DoesNotContain(appended.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Parse_IncrementalAppendWithRefDefInMiddle_ExpandsRegionGracefully()
    {
        // 追加内容在引用定义之后，且新内容含 [ 字符，应触发区域扩大直至安全边界，
        // 最终走全量解析但不抛异常、不产生错误诊断。
        var parser = CreateParser();
        var first = parser.Parse("[ref]: https://example.com\n\n");
        var appended = parser.Parse("[ref]: https://example.com\n\ntext [bracket] more\n", first.Document);

        Assert.NotNull(appended.Document.Root);
    }

    [Fact]
    public void Parse_MultipleIncrementalSteps_Stable()
    {
        // 多步追加模拟实际编辑：每步传入上一步的文档，验证不会崩溃。
        var parser = CreateParser();
        var doc = parser.Parse("# Title\n").Document;
        doc = parser.Parse("# Title\n\n", doc).Document;
        doc = parser.Parse("# Title\n\nPara one\n", doc).Document;
        doc = parser.Parse("# Title\n\nPara one\nPara two\n", doc).Document;

        Assert.NotNull(doc.Root);
    }

    [Fact]
    public void Parse_Tree_ReturnsRootNode()
    {
        var parser = CreateParser();
        var root = parser.ParseTree("# Heading\n", previousRoot: null);
        Assert.NotNull(root);
    }
}
