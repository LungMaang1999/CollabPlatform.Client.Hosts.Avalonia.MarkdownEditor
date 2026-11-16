using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
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

    /// <summary>
    /// 生效属性值（来源不是 DefinitionDefault 的用户属性）。旧包没有来源信息，
    /// 读取时按 <c>Instance</c> 处理。
    /// </summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 与 <see cref="Attributes"/> 对应的来源标记；只记录非 Instance 的来源
    /// （Source），缺省即 Instance。定义默认值不落盘，解析时按定义重新注入。
    /// </summary>
    public Dictionary<string, string> AttributeSources { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 实例层中当前被源码值覆盖的回退值（键仍在 <see cref="Attributes"/> 里给出有效值）。
    /// 保证“删除源码值后回落到实例值”跨保存/打开仍然成立。
    /// </summary>
    public Dictionary<string, string> InstanceAttributes { get; } = new(StringComparer.Ordinal);

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

    /// <summary>类型定义的 kind 限定（自定义元素按 custom-kind 匹配）；可空，旧包不受影响。</summary>
    public string? AppliesToKind { get; set; }
    public NodeStyleDto Style { get; set; } = new();

    /// <summary>类型级属性声明，仅 AppliesTo 非空时落盘。</summary>
    public List<ElementAttributeDeclarationDto> Attributes { get; } = new();

    /// <summary>类型级渲染行为，仅 AppliesTo 非空时落盘。</summary>
    public ElementRenderBehaviorDto Behavior { get; set; } = new();
}

internal sealed class ElementAttributeDeclarationDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string ValueKind { get; set; } = "Text";
    public string? DefaultValue { get; set; }
    public List<string> Options { get; } = new();
    public string Hint { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string Target { get; set; } = "DocumentAttribute";
    public bool IsDerived { get; set; }
    public string Group { get; set; } = string.Empty;

    public string? BindingTarget { get; set; }
    public string? BindingName { get; set; }
    public string? BindingPart { get; set; }
    public string? BindingValueMap { get; set; }

    public ElementAttributePresentationDto? Presentation { get; set; }
}

/// <summary>声明属性的编辑侧呈现设置（DTO）。</summary>
public sealed class ElementAttributePresentationDto
{
    public string Control { get; set; } = nameof(ElementAttributeControlKind.Auto);
    public string? Placeholder { get; set; }
    public string? Unit { get; set; }
    public string Width { get; set; } = nameof(ElementAttributeFieldWidth.Auto);
    public int? Order { get; set; }
    public bool IsAdvanced { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }
    public int? Precision { get; set; }
    public int? Rows { get; set; }
    public int? MaxLength { get; set; }
    public string? Pattern { get; set; }
    public List<ElementAttributeVisibilityConditionDto> VisibleWhen { get; set; } = [];
}

/// <summary>条件显示的一项（DTO）。</summary>
public sealed class ElementAttributeVisibilityConditionDto
{
    public string Key { get; set; } = string.Empty;
    public string Operator { get; set; } = nameof(ElementAttributeConditionOperator.Equals);
    public string? Value { get; set; }

    /// <summary>无法识别的运算符原文；写回时优先使用。</summary>
    public string? RawOperator { get; set; }
}

internal sealed class ElementRenderBehaviorDto
{
    public string FrameStrategy { get; set; } = "Auto";

    public string TemplateVariant { get; set; } = "Default";
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

    public string? VerticalAlign { get; set; }
    public ThicknessValueDto? Margin { get; set; }
    public ThicknessValueDto? Padding { get; set; }
    public string? BorderColor { get; set; }
    public double? BorderWidth { get; set; }
    public double? BorderRadius { get; set; }
    public double? Width { get; set; }
    public double? Gap { get; set; }
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
