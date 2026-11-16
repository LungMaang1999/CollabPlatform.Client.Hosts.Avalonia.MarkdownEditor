namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// 节点属性值的来源层级。枚举值是优先级：数值越小越优先。
/// <list type="number">
/// <item><see cref="Source"/>：源码内联（<c>:::note level=info</c>）；</item>
/// <item><see cref="Instance"/>：实例层写入（属性面板/命令/持久化恢复）；</item>
/// <item><see cref="DefinitionDefault"/>：由定义声明注入的默认值。</item>
/// </list>
/// 合并规则：同一键同时存在多种来源时取最小枚举值；定义默认值只在
/// 既无源码值也无实例值时注入。
/// </summary>
public enum AttributeValueSource
{
    Source = 0,
    Instance = 1,
    DefinitionDefault = 2
}

public static class AttributeValueSourceExtensions
{
    /// <summary>候选来源是否应当覆盖当前来源（数值更小者胜）。</summary>
    public static bool Outranks(this AttributeValueSource candidate, AttributeValueSource current) =>
        candidate < current;
}
