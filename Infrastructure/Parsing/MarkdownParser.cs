using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Parsing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Factories;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax.Matching;
using Markdig;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

public sealed class MarkdownParser : IMarkdownParser
{
    private readonly MarkdownParserOptions _options;
    private readonly NodeFactory _nodeFactory;
    private readonly SectionBuilder _sectionBuilder;
    private readonly INodeIdentityMatcher _identityMatcher;
    private readonly MarkdigToNodeConverter _converter;
    private readonly MarkdownPipeline _pipeline;

    public MarkdownParser(
        MarkdownParserOptions options,
        NodeFactory? nodeFactory = null,
        SectionBuilder? sectionBuilder = null,
        INodeIdentityMatcher? identityMatcher = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nodeFactory = nodeFactory ?? new NodeFactory();
        _sectionBuilder = sectionBuilder ?? new SectionBuilder(_nodeFactory);
        _identityMatcher = identityMatcher ?? new NodeIdentityMatcher();
        _converter = new MarkdigToNodeConverter(_nodeFactory);

        var pipelineBuilder = new MarkdownPipelineBuilder();
        if (_options.EnablePipeTables) pipelineBuilder.UsePipeTables();
        if (_options.EnableTaskLists) pipelineBuilder.UseTaskLists();
        if (_options.EnableFootnotes) pipelineBuilder.UseFootnotes();
        if (_options.EnableYamlFrontMatter) pipelineBuilder.UseYamlFrontMatter();

        pipelineBuilder.UseEmphasisExtras();

        _pipeline = pipelineBuilder.Build();
    }

    public ParseResult Parse(
        string source,
        MarkdownDocument? previous = null,
        ParseMode mode = ParseMode.Editing)
    {
        source ??= string.Empty;

        var diagnostics = new List<DiagnosticMessage>();
        var blocks = ParseBlocks(source, diagnostics);
        var root = _sectionBuilder.Build(blocks);

        MarkdownDocument document;

        if (previous is null)
        {
            document = new MarkdownDocument();
        }
        else
        {
            document = previous;
            _identityMatcher.Match(document.Root, root);
        }

        var markChanged = mode == ParseMode.Editing;
        document.ReplaceParsedRoot(root, source, markChanged);

        if (mode == ParseMode.Loading)
        {
            document.MarkSaved();
        }

        return new ParseResult(document, diagnostics);
    }

    private IReadOnlyList<MarkdownNode> ParseBlocks(
        string source,
        ICollection<DiagnosticMessage> diagnostics)
    {
        var markdigDoc = Markdown.Parse(source, _pipeline);
        return _converter.ConvertBlocks(markdigDoc, diagnostics);
    }
}