using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Parsing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Factories;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;
using Markdig;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.SyntaxTakeover;

/// <summary>
/// 影子解析链：与生产 <see cref="MarkdownParser"/> 并行运行，
/// 只替换围栏识别器（<see cref="FenceDispatchParser"/>、
/// <see cref="ShadowUserBlockParser"/>），转换器、章节装配、渲染器全部复用同一条实现。
/// 仅供对照双跑，不进入任何生产路径；关闭即回到 MarkdownParser。
/// </summary>
public static class ShadowSyntaxTakeover
{
    /// <summary>
    /// 按生产管线的注册次序构建影子管线：选项扩展逐一对应，
    /// 仅把两个围栏解析器换成影子实现，其余扩展点顺序保持逐项一致。
    /// </summary>
    public static MarkdownPipeline BuildPipeline(
        MarkdownParserOptions options,
        UserBlockRegistry registry,
        IReadOnlyList<UserBlockDefinition>? definitions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);

        var builder = new MarkdownPipelineBuilder();
        if (options.EnablePipeTables) builder.UsePipeTables();
        if (options.EnableTaskLists) builder.UseTaskLists();
        if (options.EnableFootnotes) builder.UseFootnotes();
        if (options.EnableYamlFrontMatter) builder.UseYamlFrontMatter();
        if (options.EnableEmphasisExtras) builder.UseEmphasisExtras();
        if (options.EnableAutoLinks) builder.UseAutoLinks();
        if (options.EnableMathematics)
        {
            builder.UseMathematics();
            builder.BlockParsers.Insert(0, new LatexBlockMathParser());
        }

        // 与生产 CreateRuntime 共用同一个行内注册入口，两边次序不可能分叉。
        InlineParserRegistration.Apply(builder, options);

        // 与生产同序：带中间文字的分割线在 ThematicBreakParser 之前；标签为空时交还原生分割线。
        builder.BlockParsers.Insert(0, new DividerParser());
        builder.BlockParsers.Insert(0, new AnchorMatrixParser());

        // 与生产 CreateRuntime 同一份数据：内置数据面与文档定义合并后重建关键字表，
        // 块类型走块工厂表。
        var declarations = MarkdownParser.MergeBuiltInDeclarations(
            BuiltInElementDefinitions.Load(),
            definitions);
        var fenceKeywords = FenceKeywordRegistry.Create(declarations);
        var primeFenceParser = new FenceDispatchParser(
            fenceKeywords,
            MarkdownParser.ResolveItemMarkers(declarations));
        builder.BlockParsers.Insert(0, primeFenceParser);
        // 与生产管线同序：折叠、目录两个容器块都先于单引号分派器。
        builder.BlockParsers.Insert(0, new CollapseParser());
        builder.BlockParsers.Insert(0, new TocpicParser());
        builder.BlockParsers.Insert(0, new ColorFenceParser());

        // 与生产 CreateRuntime 同一注册次序：单引号族在内置之后，其余字符（含代码围栏族）在之前。
        foreach (var fenceChar in registry.DeclaredFenceChars.Distinct())
        {
            if (fenceChar == UserBlockRegistry.PrimeFenceChar)
            {
                var primeIndex = builder.BlockParsers.IndexOf(primeFenceParser) + 1;
                builder.BlockParsers.Insert(primeIndex, new ShadowUserBlockParser(registry, fenceChar));
                continue;
            }

            builder.BlockParsers.Insert(0, new ShadowUserBlockParser(registry, fenceChar));
        }

        return builder.Build();
    }

    /// <summary>
    /// 影子链的文档装配：与生产 <see cref="MarkdownParser.Parse"/> 同一顺序
    /// （转换 → 章节装配 → 换根 → 记定义 → 注入定义默认值），
    /// 差别只在语法识别由影子解析器完成。
    /// </summary>
    public static ParseResult Parse(
        string source,
        MarkdownParserOptions? options = null,
        IReadOnlyList<UserBlockDefinition>? definitions = null,
        ParseMode mode = ParseMode.Editing,
        MarkdownDocument? previous = null)
    {
        source ??= string.Empty;
        options ??= new MarkdownParserOptions();

        var resolved = definitions ?? options.UserBlocks ?? [];
        var registry = new UserBlockRegistry(resolved);
        var pipeline = BuildPipeline(options, registry, resolved);
        var nodeFactory = new NodeFactory();
        // 生产线与影子线必须是同一份数据：行内/块级 HTML 白名单同样取合并后的声明。
        var merged = MarkdownParser.MergeBuiltInDeclarations(BuiltInElementDefinitions.Load(), resolved);
        var inlineHtml = MarkdownParser.ResolveInlineHtmlDefinition(merged);
        var blockHtml = MarkdownParser.ResolveBlockHtmlDefinition(merged);
        var converter = new MarkdigToNodeConverter(
            nodeFactory,
            registry,
            inlineHtml: inlineHtml,
            blockHtml: blockHtml);
        var diagnostics = new List<DiagnosticMessage>();

        MarkdownNode root;
        try
        {
            var blocks = ParsedDocumentPipeline.ParseAndConvert(
                source,
                pipeline,
                converter,
                registry,
                diagnostics);
            root = new SectionBuilder(nodeFactory).Build(blocks);

            // 与生产管线同口径：标题锚点必须在整棵树拼好之后统一重算。
            HeadingAnchorResolver.Apply(root);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Error,
                Code = "MARKDOWN_PARSE_FAILED",
                Message = ex.Message,
                Range = SourceRange.Empty()
            });

            root = new SectionBuilder(nodeFactory).Build(Array.Empty<MarkdownNode>());
        }

        var document = previous ?? new MarkdownDocument();
        document.ReplaceParsedRoot(root, source, markChanged: mode == ParseMode.Editing);
        document.ApplyUserBlockDefinitions(resolved);
        document.ApplyDeclaredAttributeDefaults();

        return new ParseResult(document, diagnostics);
    }
}
