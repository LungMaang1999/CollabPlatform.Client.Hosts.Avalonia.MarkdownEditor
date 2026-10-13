using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

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

    /// <summary>
    /// 序列化为送往 WebView 的 JSON 字符串。
    /// 直接使用 Utf8JsonWriter 手写固定契约：Browser/WASM 裁剪或 AOT 环境下基于反射的
    /// JsonSerializer 被禁用（抛 JsonSerializersReflectionDisabled），此热路径不得依赖反射。
    /// </summary>
    public string ToPayloadJson()
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            // 不转义非 ASCII（中文等），避免补丁载荷体积成倍膨胀；HTML 内的 < > 等同样原样输出。
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("ops");
            writer.WriteStartArray();
            foreach (var op in Ops)
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "t",
                    op.Kind switch
                    {
                        PreviewPatchOpKind.Upsert => "i",
                        PreviewPatchOpKind.Move => "m",
                        PreviewPatchOpKind.Remove => "d",
                        _ => throw new InvalidOperationException()
                    });
                writer.WriteString("id", op.Id);
                // 与旧契约一致：null 字段直接缺省。
                if (op.AfterId is not null)
                {
                    writer.WriteString("after", op.AfterId);
                }

                if (op.Html is not null)
                {
                    writer.WriteString("html", op.Html);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>从载荷 JSON 还原（测试与宿主演练契约用）。同样基于 JsonDocument，无反射。</summary>
    public static PreviewPatch FromPayloadJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("预览补丁载荷不是 JSON 对象。");
        }

        if (!root.TryGetProperty("ops", out var opsElement) || opsElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("预览补丁载荷缺少 ops 数组。");
        }

        var ops = new List<PreviewPatchOp>(opsElement.GetArrayLength());
        foreach (var element in opsElement.EnumerateArray())
        {
            string? type = element.TryGetProperty("t", out var typeElement) ? typeElement.GetString() : null;
            if (type is null)
            {
                throw new JsonException("补丁操作缺少 t 字段。");
            }

            string? id = element.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (id is null)
            {
                throw new JsonException("补丁操作缺少 id 字段。");
            }
            var after = element.TryGetProperty("after", out var afterElement) ? afterElement.GetString() : null;
            var html = element.TryGetProperty("html", out var htmlElement) ? htmlElement.GetString() : null;

            ops.Add(new PreviewPatchOp(
                id,
                type switch
                {
                    "i" => PreviewPatchOpKind.Upsert,
                    "m" => PreviewPatchOpKind.Move,
                    "d" => PreviewPatchOpKind.Remove,
                    _ => throw new JsonException($"未知补丁操作类型：{type}")
                },
                after,
                html));
        }

        return new PreviewPatch(ops);
    }
}
