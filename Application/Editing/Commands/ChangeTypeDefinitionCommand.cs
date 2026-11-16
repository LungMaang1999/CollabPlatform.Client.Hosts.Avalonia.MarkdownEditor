using System;
using System.Collections.Generic;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

/// <summary>
/// 类型级定义的不可变快照：样式、渲染行为与属性声明。
/// 命令与宿主页面都通过它做差异比较和撤销恢复。
/// </summary>
public sealed class ElementTypeDefinitionState
{
    public ElementTypeDefinitionState(
        NodeStyle style,
        ElementRenderBehavior behavior,
        IEnumerable<ElementAttributeDeclaration> attributes)
    {
        Style = style ?? throw new ArgumentNullException(nameof(style));
        Behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
        Attributes = CloneAttributes(attributes);
    }

    public NodeStyle Style { get; }

    public ElementRenderBehavior Behavior { get; }

    public IReadOnlyList<ElementAttributeDeclaration> Attributes { get; }

    public static ElementTypeDefinitionState Empty() =>
        new(new NodeStyle(), new ElementRenderBehavior(), []);

    /// <summary>读取指定样式定义的快照；定义不存在时返回 null。</summary>
    public static ElementTypeDefinitionState? Capture(StyleSheet sheet, string? styleId)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var definition = sheet.FindById(styleId);
        return definition is null
            ? null
            : new ElementTypeDefinitionState(
                definition.Style.Clone(),
                definition.Behavior.Clone(),
                definition.Attributes);
    }

    public static bool AreEqual(
        ElementTypeDefinitionState? left,
        ElementTypeDefinitionState? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (!StyleEquals(left.Style, right.Style)) return false;
        if (left.Behavior.FrameStrategy != right.Behavior.FrameStrategy) return false;
        if (left.Behavior.TemplateVariant != right.Behavior.TemplateVariant) return false;
        if (left.Attributes.Count != right.Attributes.Count) return false;

        for (var i = 0; i < left.Attributes.Count; i++)
        {
            if (!AttributeEquals(left.Attributes[i], right.Attributes[i])) return false;
        }

        return true;
    }

    internal static List<ElementAttributeDeclaration> CloneAttributes(
        IEnumerable<ElementAttributeDeclaration> attributes)
    {
        var result = new List<ElementAttributeDeclaration>();
        foreach (var attribute in attributes)
        {
            result.Add(attribute.Clone());
        }

        return result;
    }

    private static bool StyleEquals(NodeStyle a, NodeStyle b) =>
        string.Equals(a.FontFamily, b.FontFamily, StringComparison.Ordinal)
        && a.FontSize == b.FontSize
        && a.LineHeight == b.LineHeight
        && string.Equals(a.TextAlign, b.TextAlign, StringComparison.Ordinal)
        && string.Equals(a.VerticalAlign, b.VerticalAlign, StringComparison.Ordinal)
        && a.Bold == b.Bold
        && a.Italic == b.Italic
        && string.Equals(a.ForegroundColor, b.ForegroundColor, StringComparison.Ordinal)
        && string.Equals(a.BackgroundColor, b.BackgroundColor, StringComparison.Ordinal)
        && string.Equals(a.BorderColor, b.BorderColor, StringComparison.Ordinal)
        && a.BorderWidth == b.BorderWidth
        && a.BorderRadius == b.BorderRadius
        && a.Width == b.Width
        && a.Gap == b.Gap
        && Equals(a.Margin, b.Margin)
        && Equals(a.Padding, b.Padding)
        && string.Equals(a.CustomCss, b.CustomCss, StringComparison.Ordinal);

    private static bool AttributeEquals(
        ElementAttributeDeclaration a,
        ElementAttributeDeclaration b) =>
        string.Equals(a.Key, b.Key, StringComparison.Ordinal)
        && string.Equals(a.Label, b.Label, StringComparison.Ordinal)
        && a.ValueKind == b.ValueKind
        && string.Equals(a.DefaultValue, b.DefaultValue, StringComparison.Ordinal)
        && string.Equals(a.Hint, b.Hint, StringComparison.Ordinal)
        && a.IsEnabled == b.IsEnabled
        && a.Target == b.Target
        && a.IsDerived == b.IsDerived
        && string.Equals(a.Group, b.Group, StringComparison.Ordinal)
        && OptionsEqual(a.Options, b.Options)
        && BindingEquals(a.Binding, b.Binding)
        && PresentationEquals(a.Presentation, b.Presentation);

    private static bool PresentationEquals(
        ElementAttributePresentation? a,
        ElementAttributePresentation? b) =>
        string.Equals(
            a is null ? string.Empty : ElementAttributePresentationEvaluator.Describe(a),
            b is null ? string.Empty : ElementAttributePresentationEvaluator.Describe(b),
            StringComparison.Ordinal);

    private static bool BindingEquals(
        ElementAttributeRenderBinding? a,
        ElementAttributeRenderBinding? b)
    {
        if (a is null || a.IsEmpty) return b is null || b.IsEmpty;
        if (b is null || b.IsEmpty) return false;

        return a.Target == b.Target
               && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
               && string.Equals(a.Part, b.Part, StringComparison.Ordinal)
               && string.Equals(a.ValueMap, b.ValueMap, StringComparison.Ordinal);
    }

    private static bool OptionsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        }

        return true;
    }
}

/// <summary>
/// 类型级定义变更：写入 <see cref="StyleDefinition.Style"/>、属性声明与渲染行为。
/// 首次提交会自动创建类型样式定义，撤销时若定义由本命令创建则一并移除。
/// </summary>
public sealed class ChangeTypeDefinitionCommand : IEditorCommand, IEditorCommandState
{
    private readonly MarkdownDocument _document;
    private readonly string _styleId;
    private readonly NodeType _appliesTo;
    private readonly string _name;
    private readonly ElementTypeDefinitionState? _oldState;
    private readonly ElementTypeDefinitionState _newState;

    public ChangeTypeDefinitionCommand(
        MarkdownDocument document,
        string styleId,
        NodeType appliesTo,
        string name,
        ElementTypeDefinitionState? oldState,
        ElementTypeDefinitionState newState)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        ArgumentException.ThrowIfNullOrWhiteSpace(styleId);
        _styleId = styleId;
        _appliesTo = appliesTo;
        _name = name ?? string.Empty;
        _oldState = oldState;
        _newState = newState ?? throw new ArgumentNullException(nameof(newState));
        HasChanges = !ElementTypeDefinitionState.AreEqual(oldState, newState);
    }

    public string Description => $"Change element type definition '{_appliesTo}'";

    public bool HasChanges { get; private set; }

    public void Execute()
    {
        var definition = EnsureDefinition();
        Apply(definition, _newState);
        // 声明表变了，生效属性里的定义默认要同步更新；放在命令内保证撤销/重做同样生效，
        // 也让命令状态变化的监听方（画布/面板）读到已注入的值。
        _document.ApplyDeclaredAttributeDefaults();
        HasChanges = true;
        _document.MarkStyleChanged();
    }

    public void Undo()
    {
        var definition = _document.StyleSheet.FindById(_styleId);
        if (_oldState is null)
        {
            // 由本命令创建：撤销即移除，避免空类型定义留在样式表里。
            if (definition is not null)
            {
                _document.StyleSheet.Styles.Remove(definition);
            }
        }
        else if (definition is not null)
        {
            Apply(definition, _oldState);
        }

        // 撤销/重做同样要恢复声明对应的定义默认（新增/删除/改值三态）。
        _document.ApplyDeclaredAttributeDefaults();
        _document.MarkStyleChanged();
    }

    private StyleDefinition EnsureDefinition()
    {
        var definition = _document.StyleSheet.FindById(_styleId);
        if (definition is not null) return definition;

        definition = new StyleDefinition
        {
            Id = _styleId,
            Name = _name,
            AppliesTo = _appliesTo,
        };
        _document.StyleSheet.Styles.Add(definition);
        return definition;
    }

    private static void Apply(StyleDefinition definition, ElementTypeDefinitionState state)
    {
        definition.Style = state.Style.Clone();
        definition.Behavior = state.Behavior.Clone();
        definition.Attributes.Clear();
        foreach (var attribute in state.Attributes)
        {
            definition.Attributes.Add(attribute.Clone());
        }
    }
}
