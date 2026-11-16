using System.Collections.ObjectModel;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;

/// <summary>
/// 内置元素描述的对外入口：宿主程序集只通过这里读取内置类型清单。
/// 数据仍来自 <c>builtin-elements.xml</c> 这一份嵌入资源，不另建清单；
/// 宿主不再需要 <c>InternalsVisibleTo</c> 来触达引擎内部实现。
/// </summary>
public static class BuiltInElementCatalog
{
    /// <summary>全部内置元素定义（只读视图，调用方无法改写）。</summary>
    public static IReadOnlyList<UserBlockDefinition> All { get; } =
        new ReadOnlyCollection<UserBlockDefinition>(BuiltInElementDefinitions.Load().ToArray());
}
