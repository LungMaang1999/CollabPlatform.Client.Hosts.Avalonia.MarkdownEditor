using System;
using System.Collections.Generic;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Rendering.Graph;

/// <summary>
/// 图渲染适配层：只服务测试，不接管产品渲染路径。
/// 它把“一个提示块实例”交给图求值；正文由唯一的渲染器按真实子树产出，不再从外部注入。
/// </summary>
internal static class NodeGraphQuoteRenderer
{
    private static readonly GraphNodeRegistry Registry = NodeGraphStageLibrary.CreateRegistry();

    public static GraphEvaluationResult Render(MarkdownDocument document, MarkdownNode quote)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(quote);

        // 变体由文档内该类型的行为决定，投影只负责结构，不再携带变体快照。
        var graph = NodeGraphMigration.CreateDefaultProjection(NodeType.Quote, Registry);

        var instanceValues = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in quote.Attributes)
        {
            instanceValues[pair.Key] = pair.Value;
        }

        return new NodeGraphEvaluator(Registry).Evaluate(graph, new GraphEvaluationOptions
        {
            RootNode = quote,
            Document = document,
            InstanceValues = instanceValues,
        });
    }
}
