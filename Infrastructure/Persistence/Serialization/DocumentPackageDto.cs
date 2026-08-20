using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Schema;
using System;
using System.Collections.Generic;
using System.Text;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

internal sealed class DocumentPackageDto
{
    public string SchemaVersion { get; set; } = SchemaMigrator.CurrentSchemaVersion;
    public string EditorVersion { get; set; } = SchemaMigrator.CurrentEditorVersion;
    public DocumentDto Document { get; set; } = new();
}

internal sealed class DocumentDto
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public SourceDto Source { get; set; } = new();
    public List<StyleDefinitionDto> Styles { get; } = new();
    public NodeDto? Root { get; set; }
    public EditorStateDto EditorState { get; set; } = new();
    public MetadataDto Metadata { get; set; } = new();
}

internal sealed class SourceDto
{
    public string Path { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string Encoding { get; set; } = "utf-8";
}

internal sealed class NodeDto
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string RawMarkdown { get; set; } = string.Empty;
    public int? Level { get; set; }
    public SourceRangeDto Range { get; set; } = new();
    public string? StyleId { get; set; }
    public bool IsTableHeader { get; set; }
    public NodeStyleDto LocalStyle { get; set; } = new();
    public bool IsSynthetic { get; set; }
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);
    public List<NodeDto> Children { get; } = new();
}

internal sealed class SourceRangeDto
{
    public int StartOffset { get; set; }
    public int Length { get; set; }
    public int StartLine { get; set; }
    public int StartColumn { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
}

internal sealed class StyleDefinitionDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ParentStyleId { get; set; }
    public string? AppliesTo { get; set; }
    public NodeStyleDto Style { get; set; } = new();
}

internal sealed class NodeStyleDto
{
    public string? FontFamily { get; set; }
    public double? FontSize { get; set; }
    public string? ForegroundColor { get; set; }
    public string? BackgroundColor { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public double? LineHeight { get; set; }
    public string? TextAlign { get; set; }
    public ThicknessValueDto? Margin { get; set; }
    public ThicknessValueDto? Padding { get; set; }
    public string? BorderColor { get; set; }
    public double? BorderWidth { get; set; }
    public string? CustomCss { get; set; }
}

internal sealed class ThicknessValueDto
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Right { get; set; }
    public double Bottom { get; set; }
}

internal sealed class EditorStateDto
{
    public string? SelectedNodeId { get; set; }
    public int CaretOffset { get; set; }
    public int SelectionLength { get; set; }
    public HashSet<string> ExpandedNodeIds { get; } = new(StringComparer.Ordinal);
}

internal sealed class MetadataDto
{
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public string Encoding { get; set; } = "utf-8";
    public DateTime CreatedUtc { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);
}