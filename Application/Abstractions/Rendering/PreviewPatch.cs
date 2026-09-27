using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;

/// <summary>
/// 一帧预览增量补丁：按顺序应用的顶层块操作列表。
/// 载荷 JSON 为最小字段契约：<c>{"ops":[{"t":"i|m|d","id":"...","after":"...?","html":"...?"}]}</c>。
/// </summary>
public sealed class PreviewPatch
{
    public IReadOnlyList<PreviewPatchOp> Ops { get; }

    public PreviewPatch(IReadOnlyList<PreviewPatchOp> ops)
    {
        Ops = ops ?? throw new ArgumentNullException(nameof(ops));
    }

    /// <summary>序列化为送往 WebView 的 JSON 字符串。</summary>
    public string ToPayloadJson()
    {
        var dtoOps = new PayloadOpDto[Ops.Count];
        for (var i = 0; i < Ops.Count; i++)
        {
            var op = Ops[i];
            dtoOps[i] = new PayloadOpDto(
                op.Kind switch
                {
                    PreviewPatchOpKind.Upsert => "i",
                    PreviewPatchOpKind.Move => "m",
                    PreviewPatchOpKind.Remove => "d",
                    _ => throw new InvalidOperationException()
                },
                op.Id,
                op.AfterId,
                op.Html);
        }

        return JsonSerializer.Serialize(new PayloadDto(dtoOps), PayloadJsonOptions);
    }

    /// <summary>从载荷 JSON 还原（测试与宿主演练契约用）。</summary>
    public static PreviewPatch FromPayloadJson(string json)
    {
        var dto = JsonSerializer.Deserialize<PayloadDto>(json, PayloadJsonOptions)
                  ?? throw new JsonException("预览补丁载荷为空。");

        var ops = new List<PreviewPatchOp>(dto.Ops?.Length ?? 0);
        if (dto.Ops is not null)
        {
            foreach (var op in dto.Ops)
            {
                ops.Add(new PreviewPatchOp(
                    op.Id,
                    op.T switch
                    {
                        "i" => PreviewPatchOpKind.Upsert,
                        "m" => PreviewPatchOpKind.Move,
                        "d" => PreviewPatchOpKind.Remove,
                        _ => throw new JsonException($"未知补丁操作类型：{op.T}")
                    },
                    op.After,
                    op.Html));
            }
        }

        return new PreviewPatch(ops);
    }

    internal static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 不转义非 ASCII（中文等），避免补丁载荷体积成倍膨胀；HTML 内的 < > 等同样原样输出。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed record PayloadDto([property: JsonPropertyName("ops")] PayloadOpDto[] Ops);

    private sealed record PayloadOpDto(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("after")] string? After,
        [property: JsonPropertyName("html")] string? Html);
}
