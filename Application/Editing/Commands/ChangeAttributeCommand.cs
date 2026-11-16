using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

/// <summary>
/// 属性面板/命令写入节点属性。写入落在实例层（来源 <see cref="AttributeValueSource.Instance"/>）：
/// 源码已声明的键保持源码值生效，实例值作为“删除源码值后的回落”保留；
/// <c>value == null</c> 表示“恢复默认”：删除实例层条目并让定义默认值重新生效。
/// 撤销/重做同时翻转值与来源，而不只是值。
/// </summary>
public sealed class ChangeAttributeCommand : IEditorCommand, IEditorCommandState
{
    private readonly MarkdownDocument _document;
    private readonly MarkdownNode _node;
    private readonly string _nodeId;
    private readonly string _key;
    private readonly string? _newValue;
    private readonly bool _hadOldValue;
    private readonly string? _oldValue;
    private readonly AttributeValueSource _oldSource;
    private readonly string? _oldInstanceValue;

    public string Description => $"Change attribute '{_key}'";
    public bool HasChanges { get; private set; }

    public ChangeAttributeCommand(MarkdownDocument document, MarkdownNode node, string key, string? value)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _node = node ?? throw new ArgumentNullException(nameof(node));
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _nodeId = node.Id;
        _key = key;
        _newValue = value;
        _hadOldValue = node.Attributes.TryGetValue(key, out _oldValue);
        _oldSource = node.GetAttributeSource(key);
        node.InstanceAttributes.TryGetValue(key, out _oldInstanceValue);
    }

    public void Execute()
    {
        var node = ResolveNode();
        if (_newValue is null)
        {
            ApplyDefault(node);
        }
        else
        {
            node.SetInstanceAttribute(_key, _newValue);
        }

        HasChanges = true;
        _document.MarkStyleChanged();
    }

    public void Undo()
    {
        var node = ResolveNode();
        node.RestoreAttribute(_key, _hadOldValue ? _oldValue : null, _oldSource);
        node.RestoreInstanceAttribute(_key, _oldInstanceValue);
        _document.MarkStyleChanged();
    }

    /// <summary>
    /// 恢复默认：删除实例层条目。源码值仍在时保持源码值；
    /// 否则回落到定义声明的默认值（无默认值则移除该键）。
    /// </summary>
    private void ApplyDefault(MarkdownNode node)
    {
        node.RemoveInstanceAttribute(_key);
        if (node.GetAttributeSource(_key) == AttributeValueSource.Source)
        {
            return;
        }

        // 元素定义默认优先，其次类型样式声明的默认值（与渲染优先级一致）。
        var fallback = _document.GetUserBlockAttributeDefault(node, _key)
                       ?? _document.GetDeclaredAttributeDefault(node, _key);
        if (fallback is null)
        {
            node.RemoveAttribute(_key);
            return;
        }

        node.RemoveAttribute(_key);
        node.SetDefinitionDefaultAttribute(_key, fallback);
    }

    // 撤销/重做会重建 AST（ReplaceParsedRoot 换根），构造期捕获的节点会脱离新树；
    // 每次执行都按 Id 从当前树重新解析，否则重做只作用于游离旧节点而不生效。
    private MarkdownNode ResolveNode() => _document.FindNode(_nodeId) ?? _node;
}
