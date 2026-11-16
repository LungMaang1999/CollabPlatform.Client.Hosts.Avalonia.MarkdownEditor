using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;

/// <summary>
/// 输出记录（output record）：求值出口旁挂的只读元数据，供输出视图与检查器消费。
/// 不参与渲染、不进入块签名、不落盘；任何字段缺失只让解释退化，不让渲染退化。
/// </summary>
public sealed record OutputRecord(
    string Schema,
    OutputRecordIdentity Identity,
    IReadOnlyList<OutputRecordPart> Parts,
    IReadOnlyList<OutputRecordAttribute> Attributes,
    IReadOnlyList<OutputRecordPresentation> Presentation,
    IReadOnlyList<OutputRecordDiagnostic> Diagnostics,
    OutputRecordAnchors Anchors)
{
    public const string SchemaVersion = "output-record/1";

    public string ToJson(bool indented = true) => OutputRecordJson.Serialize(this, indented);
}

public sealed record OutputRecordIdentity(
    string InstanceKey,
    string NodeType,
    string? Kind,
    OutputRecordSourceRange SourceRange,
    string BlockSig,
    OutputRecordDefinitions Definitions,
    OutputRecordEvaluation Evaluation);

public sealed record OutputRecordSourceRange(int StartLine, int EndLine);

public sealed record OutputRecordDefinitions(
    string? Fingerprint,
    string FingerprintVersion,
    string SyntaxVersion,
    string Aggregate,
    string DocumentLevel);

public sealed record OutputRecordEvaluation(
    string Graph,
    string Status,
    IReadOnlyList<string> Order);

public sealed record OutputRecordPart(
    string Path,
    string? Tag,
    IReadOnlyList<string> Classes,
    IReadOnlyDictionary<string, string?> Attrs,
    OutputRecordCss Css,
    OutputRecordContent? Content,
    IReadOnlyList<OutputRecordPart> Children);

/// <summary>
/// 部件 CSS：<c>declared</c>/<c>bound</c>/<c>effective</c> 是值，<c>properties</c> 是逐属性来源
/// （只有根部件有；来自 <see cref="StyleResolver.ResolveProvenance"/> 的影子重放）。
/// </summary>
public sealed record OutputRecordCss(
    string? Declared,
    string? Bound,
    string? Effective,
    IReadOnlyList<OutputRecordCssProperty> Properties);

public sealed record OutputRecordCssProperty(
    string Property,
    string Value,
    string Source,
    IReadOnlyList<StylePropertyLayer> Chain);

public sealed record OutputRecordContent(string Kind, string? Value);

public sealed record OutputRecordAttribute(
    string Key,
    bool Declared,
    string ValueKind,
    string? Effective,
    string Source,
    string? Origin,
    IReadOnlyList<OutputRecordAttributeLayer> Chain,
    IReadOnlyList<OutputRecordDrive>? Drives);

public sealed record OutputRecordAttributeLayer(
    string Layer,
    bool Present,
    string? Value,
    string? OverriddenBy,
    OutputRecordAnchorRef? Ref);

public sealed record OutputRecordDrive(string Target, string Value, string Rule);

/// <summary>
/// 一个值的反查锚点。所有字段可空：拿不到就留空并写 <see cref="UnavailableReason"/>，
/// 绝不拿块级行号冒充属性级位置。
/// </summary>
public sealed record OutputRecordAnchorRef(
    int? SourceLine = null,
    int? SourceColumn = null,
    string? UnavailableReason = null,
    string? Syntax = null,
    string? Raw = null,
    string? DefinitionId = null,
    string? Field = null,
    string? InstanceKey = null,
    string? NodeId = null,
    string? Rule = null,
    OutputRecordGraphRef? Graph = null);

public sealed record OutputRecordGraphRef(string Node, string Port);

public sealed record OutputRecordPresentation(
    string Part,
    string Property,
    string? DeclaredValue,
    string EffectiveValue,
    string Source,
    OutputRecordPresentationRef Ref);

public sealed record OutputRecordPresentationRef(
    string Rule,
    string? DefinitionId = null,
    string? DeclarationKey = null,
    string? Name = null,
    string? Target = null,
    OutputRecordGraphRef? Graph = null);

public sealed record OutputRecordDiagnostic(
    string Severity,
    string Code,
    string Message,
    OutputRecordDiagnosticAnchor? Anchor = null);

public sealed record OutputRecordDiagnosticAnchor(
    int? SourceLine = null,
    string? Part = null,
    string? GraphNode = null,
    string? Port = null);

public sealed record OutputRecordAnchors(
    OutputRecordSourceAnchor Source,
    OutputRecordDefinitionAnchor? Definition,
    OutputRecordInstanceAnchor Instance,
    OutputRecordGraphAnchors Graph);

public sealed record OutputRecordSourceAnchor(string? File, int LineStart, int LineEnd);

public sealed record OutputRecordDefinitionAnchor(
    string ElementId,
    string? Field = null,
    string? FingerprintVersion = null);

public sealed record OutputRecordInstanceAnchor(string Key);

public sealed record OutputRecordGraphAnchors(
    IReadOnlyDictionary<string, string> Nodes,
    IReadOnlyDictionary<string, string> Ports);

/// <summary>记录的稳定 JSON 形态：camelCase、缩进、不转义中文。</summary>
public static class OutputRecordJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(OutputRecord record, bool indented = true) =>
        JsonSerializer.Serialize(
            record,
            indented
                ? Options
                : new JsonSerializerOptions(Options) { WriteIndented = false });
}
