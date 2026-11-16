using System.Reflection;
using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;

/// <summary>
/// 内置元素描述的唯一来源：嵌入资源 <c>builtin-elements.xml</c>。
/// 这里只负责把文件读成定义模型（与用户定义同一个反序列化入口），
/// 结构编译仍由渲染器走 <c>CreateDeclaredPartTemplate</c>。
/// </summary>
internal static class BuiltInElementDefinitions
{
    private const string ResourceSuffix = "builtin-elements.xml";

    private static readonly Lazy<IReadOnlyList<UserBlockDefinition>> Definitions =
        new(LoadCore, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<UserBlockDefinition> Load() => Definitions.Value;

    private static IReadOnlyList<UserBlockDefinition> LoadCore()
    {
        var assembly = typeof(BuiltInElementDefinitions).Assembly;
        var resourceName = Array.Find(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("缺少内置元素描述嵌入资源：" + ResourceSuffix);

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("无法读取内置元素描述嵌入资源：" + resourceName);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var file = UserBlockElementsSerializer.Deserialize(reader.ReadToEnd());
        var error = file.Diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error);
        if (error is not null)
        {
            throw new InvalidOperationException($"内置元素描述不合法：{error.Code} {error.Message}");
        }

        return file.Definitions;
    }
}
