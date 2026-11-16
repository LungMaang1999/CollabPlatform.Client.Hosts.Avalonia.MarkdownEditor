using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

internal sealed record ElementGraphAttachResult(
    IReadOnlyList<UserBlockDefinition> Definitions,
    IReadOnlyList<CompositeStageDefinition> Stages)
{
    /// <summary>伴随文件损坏时回退产生的诊断；正常路径为空。</summary>
    public IReadOnlyList<DiagnosticMessage> Diagnostics { get; init; } = [];
}

/// <summary>
/// 把 <c>.md.graph.xml</c> 里以 <c>element:{id}</c> 命名的图挂到对应元素定义上，
/// 同时取出文件里声明的复合阶段。
/// 图不复制进 elements.xml，加载时挂、保存时各存各的文件。
/// </summary>
internal static class ElementGraphFiles
{
    public const string FileSuffix = ".graph.xml";
    public const string ElementIdPrefix = "element:";

    public static string GetFilePath(string markdownFilePath) =>
        Path.GetFullPath(markdownFilePath) + FileSuffix;

    /// <summary>找不到文件或解析失败时返回原定义：没有图就按结构声明渲染，不影响打开文档。</summary>
    public static ElementGraphAttachResult Attach(
        string markdownFilePath,
        IReadOnlyList<UserBlockDefinition> definitions)
    {
        var path = GetFilePath(markdownFilePath);
        if (!File.Exists(path))
        {
            return new ElementGraphAttachResult(definitions, []);
        }

        NodeGraphReadResult read;
        try
        {
            read = NodeGraphXmlSerializer.Read(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return Fallback(definitions, "GRAPH_COMPANION_READ_FAILED",
                "元素结构图伴随文件读取失败，已按未挂图渲染。");
        }
        catch (UnauthorizedAccessException)
        {
            return Fallback(definitions, "GRAPH_COMPANION_READ_FAILED",
                "元素结构图伴随文件无权读取，已按未挂图渲染。");
        }
        catch (XmlException exception)
        {
            return Fallback(definitions, "GRAPH_COMPANION_INVALID",
                "元素结构图伴随文件不是合法 XML，已按未挂图渲染：" + exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Fallback(definitions, "GRAPH_COMPANION_INVALID",
                "元素结构图伴随文件结构非法，已按未挂图渲染：" + exception.Message);
        }

        if (definitions.Count == 0)
        {
            return new ElementGraphAttachResult(definitions, read.Stages);
        }

        var byElementId = read.Graphs
            .Where(graph => graph.Id.StartsWith(ElementIdPrefix, StringComparison.Ordinal))
            .GroupBy(graph => graph.Id[ElementIdPrefix.Length..], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        if (byElementId.Count == 0)
        {
            return new ElementGraphAttachResult(definitions, read.Stages);
        }

        var attached = definitions
            .Select(definition => byElementId.TryGetValue(definition.Id, out var graph)
                ? definition with { StructureGraph = graph }
                : definition)
            .ToList();

        return new ElementGraphAttachResult(attached, read.Stages);
    }

    /// <summary>
    /// 损坏的伴随文件不能把异常抛到打开文档的路径上：回退原定义、给一条诊断，
    /// 其余文档内容照常解析——这与“缺引用装载时丢弃”的既有风格一致。
    /// </summary>
    private static ElementGraphAttachResult Fallback(
        IReadOnlyList<UserBlockDefinition> definitions,
        string code,
        string message) => new(definitions, [])
    {
        Diagnostics =
        [
            new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Warning,
                Code = code,
                Message = message,
            },
        ],
    };
}
