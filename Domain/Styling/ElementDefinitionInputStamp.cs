using System;
using System.Collections.Generic;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Graph;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>
/// <see cref="ElementDefinitionFingerprint.ComputeSet"/> 的输入快照。
/// 只用于跳过未变化的指纹计算：漏记字段会复用过期指纹并改变 HTML，因此比对是指纹读取面的超集。
/// 多记字段或按列表顺序比对只会造成重新计算，不会改变输出。
/// 命中路径不分配。
/// </summary>
internal sealed class ElementDefinitionInputStamp
{
    private readonly StyleStamp[] _styles;
    private readonly BlockStamp[] _blocks;
    private readonly StageStamp[] _stages;

    private ElementDefinitionInputStamp(StyleStamp[] styles, BlockStamp[] blocks, StageStamp[] stages)
    {
        _styles = styles;
        _blocks = blocks;
        _stages = stages;
    }

    public static ElementDefinitionInputStamp Capture(
        StyleSheet sheet,
        IReadOnlyList<UserBlockDefinition>? userBlocks,
        IReadOnlyList<CompositeStageDefinition>? stages)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        var styles = new StyleStamp[sheet.Styles.Count];
        for (var i = 0; i < styles.Length; i++)
        {
            styles[i] = StyleStamp.Capture(sheet.Styles[i]);
        }

        var blockCount = userBlocks?.Count ?? 0;
        var blocks = new BlockStamp[blockCount];
        for (var i = 0; i < blockCount; i++)
        {
            blocks[i] = BlockStamp.Capture(userBlocks![i]);
        }

        var stageCount = stages?.Count ?? 0;
        var stageStamps = new StageStamp[stageCount];
        for (var i = 0; i < stageCount; i++)
        {
            stageStamps[i] = StageStamp.Capture(stages![i]);
        }

        return new ElementDefinitionInputStamp(styles, blocks, stageStamps);
    }

    public bool Matches(
        StyleSheet sheet,
        IReadOnlyList<UserBlockDefinition>? userBlocks,
        IReadOnlyList<CompositeStageDefinition>? stages)
    {
        var liveStyles = sheet.Styles;
        if (liveStyles.Count != _styles.Length)
        {
            return false;
        }

        for (var i = 0; i < _styles.Length; i++)
        {
            if (!_styles[i].Matches(liveStyles[i]))
            {
                return false;
            }
        }

        if (!ListMatches(userBlocks, _blocks, static (stamp, live) => stamp.Matches(live)))
        {
            return false;
        }

        return ListMatches(stages, _stages, static (stamp, live) => stamp.Matches(live));
    }

    private static bool ListMatches<TLive, TStamp>(
        IReadOnlyList<TLive>? live,
        TStamp[] stamps,
        Func<TStamp, TLive, bool> matches)
    {
        var count = live?.Count ?? 0;
        if (count != stamps.Length)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (!matches(stamps[i], live![i]))
            {
                return false;
            }
        }

        return true;
    }

    private readonly struct StyleStamp
    {
        private readonly string _id;
        private readonly NodeType? _appliesTo;
        private readonly string? _appliesToKind;
        private readonly string? _parentStyleId;
        private readonly ElementFrameStrategy _frameStrategy;
        private readonly ElementTemplateVariant _templateVariant;
        private readonly bool _hasStyle;
        private readonly StyleValueStamp _style;
        private readonly AttributeStamp[] _attributes;

        private StyleStamp(
            string id,
            NodeType? appliesTo,
            string? appliesToKind,
            string? parentStyleId,
            ElementFrameStrategy frameStrategy,
            ElementTemplateVariant templateVariant,
            bool hasStyle,
            StyleValueStamp style,
            AttributeStamp[] attributes)
        {
            _id = id;
            _appliesTo = appliesTo;
            _appliesToKind = appliesToKind;
            _parentStyleId = parentStyleId;
            _frameStrategy = frameStrategy;
            _templateVariant = templateVariant;
            _hasStyle = hasStyle;
            _style = style;
            _attributes = attributes;
        }

        public static StyleStamp Capture(StyleDefinition definition)
        {
            var attributes = new AttributeStamp[definition.Attributes.Count];
            for (var i = 0; i < attributes.Length; i++)
            {
                attributes[i] = AttributeStamp.Capture(definition.Attributes[i]);
            }

            var behavior = definition.Behavior;
            return new StyleStamp(
                definition.Id,
                definition.AppliesTo,
                definition.AppliesToKind,
                definition.ParentStyleId,
                behavior?.FrameStrategy ?? ElementFrameStrategy.Auto,
                behavior?.TemplateVariant ?? ElementTemplateVariant.Default,
                definition.Style is not null,
                definition.Style is null ? default : StyleValueStamp.Capture(definition.Style),
                attributes);
        }

        public bool Matches(StyleDefinition definition)
        {
            if (!string.Equals(_id, definition.Id, StringComparison.Ordinal)
                || _appliesTo != definition.AppliesTo
                || !string.Equals(_appliesToKind, definition.AppliesToKind, StringComparison.Ordinal)
                || !string.Equals(_parentStyleId, definition.ParentStyleId, StringComparison.Ordinal))
            {
                return false;
            }

            var behavior = definition.Behavior;
            if (behavior is null
                || behavior.FrameStrategy != _frameStrategy
                || behavior.TemplateVariant != _templateVariant
                || (definition.Style is null) != !_hasStyle)
            {
                return false;
            }

            if (_hasStyle && !_style.Matches(definition.Style!))
            {
                return false;
            }

            if (definition.Attributes.Count != _attributes.Length)
            {
                return false;
            }

            for (var i = 0; i < _attributes.Length; i++)
            {
                if (!_attributes[i].Matches(definition.Attributes[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private readonly struct StyleValueStamp
    {
        private readonly string? _fontFamily;
        private readonly double? _fontSize;
        private readonly string? _foreground;
        private readonly string? _background;
        private readonly bool? _bold;
        private readonly bool? _italic;
        private readonly double? _lineHeight;
        private readonly string? _textAlign;
        private readonly string? _verticalAlign;
        private readonly ThicknessValue? _margin;
        private readonly ThicknessValue? _padding;
        private readonly string? _borderColor;
        private readonly double? _borderWidth;
        private readonly double? _borderRadius;
        private readonly double? _width;
        private readonly double? _gap;
        private readonly string? _customCss;

        private StyleValueStamp(NodeStyle style)
        {
            _fontFamily = style.FontFamily;
            _fontSize = style.FontSize;
            _foreground = style.ForegroundColor;
            _background = style.BackgroundColor;
            _bold = style.Bold;
            _italic = style.Italic;
            _lineHeight = style.LineHeight;
            _textAlign = style.TextAlign;
            _verticalAlign = style.VerticalAlign;
            _margin = style.Margin;
            _padding = style.Padding;
            _borderColor = style.BorderColor;
            _borderWidth = style.BorderWidth;
            _borderRadius = style.BorderRadius;
            _width = style.Width;
            _gap = style.Gap;
            _customCss = style.CustomCss;
        }

        public static StyleValueStamp Capture(NodeStyle style) => new(style);

        public bool Matches(NodeStyle style) =>
            string.Equals(_fontFamily, style.FontFamily, StringComparison.Ordinal)
            && Same(_fontSize, style.FontSize)
            && string.Equals(_foreground, style.ForegroundColor, StringComparison.Ordinal)
            && string.Equals(_background, style.BackgroundColor, StringComparison.Ordinal)
            && _bold == style.Bold
            && _italic == style.Italic
            && Same(_lineHeight, style.LineHeight)
            && string.Equals(_textAlign, style.TextAlign, StringComparison.Ordinal)
            && string.Equals(_verticalAlign, style.VerticalAlign, StringComparison.Ordinal)
            && _margin == style.Margin
            && _padding == style.Padding
            && string.Equals(_borderColor, style.BorderColor, StringComparison.Ordinal)
            && Same(_borderWidth, style.BorderWidth)
            && Same(_borderRadius, style.BorderRadius)
            && Same(_width, style.Width)
            && Same(_gap, style.Gap)
            && string.Equals(_customCss, style.CustomCss, StringComparison.Ordinal);

        private static bool Same(double? left, double? right) =>
            left.HasValue == right.HasValue
            && (!left.HasValue || left.GetValueOrDefault().Equals(right.GetValueOrDefault()));
    }

    private readonly struct AttributeStamp
    {
        private readonly string _key;
        private readonly string? _defaultValue;
        private readonly bool _hasBinding;
        private readonly ElementAttributeBindingTarget _target;
        private readonly string? _name;
        private readonly string? _part;
        private readonly string? _valueMap;

        private AttributeStamp(
            string key,
            string? defaultValue,
            bool hasBinding,
            ElementAttributeBindingTarget target,
            string? name,
            string? part,
            string? valueMap)
        {
            _key = key;
            _defaultValue = defaultValue;
            _hasBinding = hasBinding;
            _target = target;
            _name = name;
            _part = part;
            _valueMap = valueMap;
        }

        public static AttributeStamp Capture(ElementAttributeDeclaration attribute)
        {
            var binding = attribute.Binding;
            return new AttributeStamp(
                attribute.Key,
                attribute.DefaultValue,
                binding is not null,
                binding?.Target ?? default,
                binding?.Name,
                binding?.Part,
                binding?.ValueMap);
        }

        public bool Matches(ElementAttributeDeclaration attribute)
        {
            if (!string.Equals(_key, attribute.Key, StringComparison.Ordinal)
                || !string.Equals(_defaultValue, attribute.DefaultValue, StringComparison.Ordinal))
            {
                return false;
            }

            var binding = attribute.Binding;
            if ((binding is null) == _hasBinding)
            {
                return false;
            }

            return binding is null
                || (binding.Target == _target
                    && string.Equals(binding.Name, _name, StringComparison.Ordinal)
                    && string.Equals(binding.Part, _part, StringComparison.Ordinal)
                    && string.Equals(binding.ValueMap, _valueMap, StringComparison.Ordinal));
        }
    }

    private readonly struct BlockStamp
    {
        private readonly string _id;
        private readonly bool _hasStructure;
        private readonly bool _allowTransparentParts;
        private readonly string[] _slots;
        private readonly PartStamp? _root;
        private readonly GraphStamp? _graph;

        private BlockStamp(
            string id,
            bool hasStructure,
            bool allowTransparentParts,
            string[] slots,
            PartStamp? root,
            GraphStamp? graph)
        {
            _id = id;
            _hasStructure = hasStructure;
            _allowTransparentParts = allowTransparentParts;
            _slots = slots;
            _root = root;
            _graph = graph;
        }

        public static BlockStamp Capture(UserBlockDefinition definition)
        {
            var structure = definition.Structure;
            string[] slots;
            PartStamp? root = null;
            var allowTransparent = false;
            if (structure is null)
            {
                slots = [];
            }
            else
            {
                allowTransparent = structure.AllowTransparentParts;
                slots = CopySlots(structure.Slots);
                if (structure.Root is not null)
                {
                    root = PartStamp.Capture(structure.Root);
                }
            }

            return new BlockStamp(
                definition.Id,
                structure is not null,
                allowTransparent,
                slots,
                root,
                definition.StructureGraph is null ? null : GraphStamp.Capture(definition.StructureGraph));
        }

        public bool Matches(UserBlockDefinition definition)
        {
            if (!string.Equals(_id, definition.Id, StringComparison.Ordinal))
            {
                return false;
            }

            var structure = definition.Structure;
            if ((structure is null) == _hasStructure)
            {
                return false;
            }

            if (structure is not null)
            {
                if (structure.AllowTransparentParts != _allowTransparentParts
                    || !SlotsMatch(structure.Slots)
                    || !PartStamp.Same(_root, structure.Root))
                {
                    return false;
                }
            }

            return GraphStamp.Same(_graph, definition.StructureGraph);
        }

        private bool SlotsMatch(IReadOnlyList<string>? slots)
        {
            var count = slots?.Count ?? 0;
            if (count != _slots.Length)
            {
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                if (!string.Equals(_slots[i], slots![i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static string[] CopySlots(IReadOnlyList<string>? slots)
        {
            var count = slots?.Count ?? 0;
            var copy = new string[count];
            for (var i = 0; i < count; i++)
            {
                copy[i] = slots![i];
            }

            return copy;
        }
    }

    private sealed class PartStamp
    {
        private readonly string _part;
        private readonly string? _include;
        private readonly string? _tag;
        private readonly bool _addressable;
        private readonly string? _when;
        private readonly string? _whenSource;
        private readonly string? _repeat;
        private readonly int _repeatMax;
        private readonly string? _generator;
        private readonly string? _generatorParams;
        private readonly string? _reference;
        private readonly string? _policy;
        private readonly bool _raw;
        private readonly string? _text;
        private readonly string? _href;
        private readonly string? _src;
        private readonly string? _class;
        private readonly string? _style;
        private readonly string? _role;
        private readonly bool _attributesAfterIdentity;
        private readonly string? _sourceCondition;
        private readonly string? _childCollection;
        private readonly string? _emitWhenCollection;
        private readonly string? _emptyCollection;
        private readonly string? _content;
        private readonly string? _css;
        private readonly string? _classRecipe;
        private readonly string? _tagCondition;
        private readonly bool _emitNodeIdentity;
        private readonly bool _selfClosing;
        private readonly bool _suppressNodeStyle;
        private readonly bool _emitNodeStyle;
        private readonly bool _emitNone;
        private readonly string[]? _declaredFields;
        private readonly PartAttributeStamp[] _attributes;
        private readonly PartStamp[] _children;

        private PartStamp(UserBlockPart part)
        {
            _part = part.Part;
            _include = part.Include;
            _tag = part.Tag;
            _addressable = part.Addressable;
            _when = part.When;
            _whenSource = part.WhenSource;
            _repeat = part.Repeat;
            _repeatMax = part.RepeatMax;
            _generator = part.Generator;
            _generatorParams = part.GeneratorParams;
            _reference = part.Ref;
            _policy = part.Policy;
            _raw = part.Raw;
            _text = part.Text;
            _href = part.Href;
            _src = part.Src;
            _class = part.Class;
            _style = part.Style;
            _role = part.Role;
            _attributesAfterIdentity = part.AttributesAfterIdentity;
            _sourceCondition = part.SourceCondition;
            _childCollection = part.ChildCollection;
            _emitWhenCollection = part.EmitWhenCollection;
            _emptyCollection = part.EmptyCollection;
            _content = part.Content;
            _css = part.Css;
            _classRecipe = part.ClassRecipe;
            _tagCondition = part.TagCondition;
            _emitNodeIdentity = part.EmitNodeIdentity;
            _selfClosing = part.SelfClosing;
            _suppressNodeStyle = part.SuppressNodeStyle;
            _emitNodeStyle = part.EmitNodeStyle;
            _emitNone = part.EmitNone;
            _declaredFields = CopySet(part.DeclaredFields);
            _attributes = CopyAttributes(part.Attributes);
            _children = new PartStamp[part.Children.Count];
            for (var i = 0; i < _children.Length; i++)
            {
                _children[i] = Capture(part.Children[i]);
            }
        }

        public static PartStamp Capture(UserBlockPart part) => new(part);

        public static bool Same(PartStamp? stamp, UserBlockPart? part) =>
            stamp is null ? part is null : part is not null && stamp.Matches(part);

        private bool Matches(UserBlockPart part)
        {
            if (!string.Equals(_part, part.Part, StringComparison.Ordinal)
                || !string.Equals(_include, part.Include, StringComparison.Ordinal)
                || !string.Equals(_tag, part.Tag, StringComparison.Ordinal)
                || _addressable != part.Addressable
                || !string.Equals(_when, part.When, StringComparison.Ordinal)
                || !string.Equals(_whenSource, part.WhenSource, StringComparison.Ordinal)
                || !string.Equals(_repeat, part.Repeat, StringComparison.Ordinal)
                || _repeatMax != part.RepeatMax
                || !string.Equals(_generator, part.Generator, StringComparison.Ordinal)
                || !string.Equals(_generatorParams, part.GeneratorParams, StringComparison.Ordinal)
                || !string.Equals(_reference, part.Ref, StringComparison.Ordinal)
                || !string.Equals(_policy, part.Policy, StringComparison.Ordinal)
                || _raw != part.Raw
                || !string.Equals(_text, part.Text, StringComparison.Ordinal)
                || !string.Equals(_href, part.Href, StringComparison.Ordinal)
                || !string.Equals(_src, part.Src, StringComparison.Ordinal)
                || !string.Equals(_class, part.Class, StringComparison.Ordinal)
                || !string.Equals(_style, part.Style, StringComparison.Ordinal)
                || !string.Equals(_role, part.Role, StringComparison.Ordinal)
                || _attributesAfterIdentity != part.AttributesAfterIdentity
                || !string.Equals(_sourceCondition, part.SourceCondition, StringComparison.Ordinal)
                || !string.Equals(_childCollection, part.ChildCollection, StringComparison.Ordinal)
                || !string.Equals(_emitWhenCollection, part.EmitWhenCollection, StringComparison.Ordinal)
                || !string.Equals(_emptyCollection, part.EmptyCollection, StringComparison.Ordinal)
                || !string.Equals(_content, part.Content, StringComparison.Ordinal)
                || !string.Equals(_css, part.Css, StringComparison.Ordinal)
                || !string.Equals(_classRecipe, part.ClassRecipe, StringComparison.Ordinal)
                || !string.Equals(_tagCondition, part.TagCondition, StringComparison.Ordinal)
                || _emitNodeIdentity != part.EmitNodeIdentity
                || _selfClosing != part.SelfClosing
                || _suppressNodeStyle != part.SuppressNodeStyle
                || _emitNodeStyle != part.EmitNodeStyle
                || _emitNone != part.EmitNone
                || !SetMatches(_declaredFields, part.DeclaredFields))
            {
                return false;
            }

            var attributes = part.Attributes;
            if (attributes.Count != _attributes.Length)
            {
                return false;
            }

            for (var i = 0; i < _attributes.Length; i++)
            {
                if (!_attributes[i].Matches(attributes[i]))
                {
                    return false;
                }
            }

            if (part.Children.Count != _children.Length)
            {
                return false;
            }

            for (var i = 0; i < _children.Length; i++)
            {
                if (!_children[i].Matches(part.Children[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static PartAttributeStamp[] CopyAttributes(IReadOnlyList<UserBlockPartAttribute>? attributes)
        {
            var count = attributes?.Count ?? 0;
            var copy = new PartAttributeStamp[count];
            for (var i = 0; i < count; i++)
            {
                copy[i] = new PartAttributeStamp(attributes![i]);
            }

            return copy;
        }

        private static string[]? CopySet(IReadOnlySet<string>? values)
        {
            if (values is null)
            {
                return null;
            }

            var copy = new string[values.Count];
            var i = 0;
            foreach (var value in values)
            {
                copy[i++] = value;
            }

            Array.Sort(copy, StringComparer.Ordinal);
            return copy;
        }

        private static bool SetMatches(string[]? stamp, IReadOnlySet<string>? live)
        {
            if (stamp is null || live is null)
            {
                return stamp is null && live is null;
            }

            if (stamp.Length != live.Count)
            {
                return false;
            }

            foreach (var value in live)
            {
                if (Array.BinarySearch(stamp, value, StringComparer.Ordinal) < 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private readonly struct PartAttributeStamp
    {
        private readonly string _name;
        private readonly string _source;
        private readonly string? _policy;

        public PartAttributeStamp(UserBlockPartAttribute attribute)
        {
            _name = attribute.Name;
            _source = attribute.Source;
            _policy = attribute.Policy;
        }

        public bool Matches(UserBlockPartAttribute attribute) =>
            string.Equals(_name, attribute.Name, StringComparison.Ordinal)
            && string.Equals(_source, attribute.Source, StringComparison.Ordinal)
            && string.Equals(_policy, attribute.Policy, StringComparison.Ordinal);
    }

    private sealed class GraphStamp
    {
        private readonly string _id;
        private readonly string _schemaVersion;
        private readonly NodeType _appliesTo;
        private readonly NodeStamp[] _nodes;
        private readonly EdgeStamp[] _edges;

        private GraphStamp(NodeGraph graph)
        {
            _id = graph.Id;
            _schemaVersion = graph.SchemaVersion;
            _appliesTo = graph.AppliesTo;
            _nodes = new NodeStamp[graph.Nodes.Count];
            for (var i = 0; i < _nodes.Length; i++)
            {
                _nodes[i] = NodeStamp.Capture(graph.Nodes[i]);
            }

            _edges = new EdgeStamp[graph.Edges.Count];
            for (var i = 0; i < _edges.Length; i++)
            {
                _edges[i] = new EdgeStamp(graph.Edges[i]);
            }
        }

        public static GraphStamp Capture(NodeGraph graph) => new(graph);

        public static bool Same(GraphStamp? stamp, NodeGraph? graph) =>
            stamp is null ? graph is null : graph is not null && stamp.Matches(graph);

        public bool Matches(NodeGraph graph)
        {
            if (!string.Equals(_id, graph.Id, StringComparison.Ordinal)
                || !string.Equals(_schemaVersion, graph.SchemaVersion, StringComparison.Ordinal)
                || _appliesTo != graph.AppliesTo
                || graph.Nodes.Count != _nodes.Length
                || graph.Edges.Count != _edges.Length)
            {
                return false;
            }

            for (var i = 0; i < _nodes.Length; i++)
            {
                if (!_nodes[i].Matches(graph.Nodes[i]))
                {
                    return false;
                }
            }

            for (var i = 0; i < _edges.Length; i++)
            {
                if (!_edges[i].Matches(graph.Edges[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private readonly struct NodeStamp
    {
        private readonly string _id;
        private readonly string _definitionId;
        private readonly bool _isBypassed;
        private readonly PortStamp[] _inputs;
        private readonly PortStamp[] _outputs;

        private NodeStamp(string id, string definitionId, bool isBypassed, PortStamp[] inputs, PortStamp[] outputs)
        {
            _id = id;
            _definitionId = definitionId;
            _isBypassed = isBypassed;
            _inputs = inputs;
            _outputs = outputs;
        }

        public static NodeStamp Capture(GraphNode node)
        {
            var inputs = new PortStamp[node.Inputs.Count];
            for (var i = 0; i < inputs.Length; i++)
            {
                inputs[i] = PortStamp.Capture(node.Inputs[i], includeFlags: true);
            }

            var outputs = new PortStamp[node.Outputs.Count];
            for (var i = 0; i < outputs.Length; i++)
            {
                outputs[i] = PortStamp.Capture(node.Outputs[i], includeFlags: false);
            }

            return new NodeStamp(node.Id, node.DefinitionId, node.IsBypassed, inputs, outputs);
        }

        public bool Matches(GraphNode node)
        {
            if (!string.Equals(_id, node.Id, StringComparison.Ordinal)
                || !string.Equals(_definitionId, node.DefinitionId, StringComparison.Ordinal)
                || _isBypassed != node.IsBypassed
                || node.Inputs.Count != _inputs.Length
                || node.Outputs.Count != _outputs.Length)
            {
                return false;
            }

            for (var i = 0; i < _inputs.Length; i++)
            {
                if (!_inputs[i].Matches(node.Inputs[i], includeFlags: true))
                {
                    return false;
                }
            }

            for (var i = 0; i < _outputs.Length; i++)
            {
                if (!_outputs[i].Matches(node.Outputs[i], includeFlags: false))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private readonly struct PortStamp
    {
        private readonly string _name;
        private readonly GraphValueKind _kind;
        private readonly bool _isRequired;
        private readonly bool _isMulti;
        private readonly bool _hasConstant;
        private readonly GraphValueKind _constantKind;
        private readonly string? _raw;
        private readonly double? _number;
        private readonly bool? _flag;

        private PortStamp(
            string name,
            GraphValueKind kind,
            bool isRequired,
            bool isMulti,
            bool hasConstant,
            GraphValueKind constantKind,
            string? raw,
            double? number,
            bool? flag)
        {
            _name = name;
            _kind = kind;
            _isRequired = isRequired;
            _isMulti = isMulti;
            _hasConstant = hasConstant;
            _constantKind = constantKind;
            _raw = raw;
            _number = number;
            _flag = flag;
        }

        public static PortStamp Capture(GraphPort port, bool includeFlags)
        {
            var constant = port.Constant;
            return new PortStamp(
                port.Name,
                port.Kind,
                includeFlags && port.IsRequired,
                includeFlags && port.IsMulti,
                constant is not null,
                constant?.Kind ?? default,
                constant?.Raw,
                constant?.Number,
                constant?.Flag);
        }

        public bool Matches(GraphPort port, bool includeFlags)
        {
            if (!string.Equals(_name, port.Name, StringComparison.Ordinal)
                || _kind != port.Kind
                || (includeFlags && (port.IsRequired != _isRequired || port.IsMulti != _isMulti)))
            {
                return false;
            }

            var constant = port.Constant;
            if ((constant is null) == _hasConstant)
            {
                return false;
            }

            return constant is null
                || (constant.Kind == _constantKind
                    && string.Equals(constant.Raw, _raw, StringComparison.Ordinal)
                    && Same(_number, constant.Number)
                    && _flag == constant.Flag);
        }

        private static bool Same(double? left, double? right) =>
            left.HasValue == right.HasValue
            && (!left.HasValue || left.GetValueOrDefault().Equals(right.GetValueOrDefault()));
    }

    private readonly struct EdgeStamp
    {
        private readonly string _fromNodeId;
        private readonly string _fromPort;
        private readonly string _toNodeId;
        private readonly string _toPort;

        public EdgeStamp(GraphEdge edge)
        {
            _fromNodeId = edge.FromNodeId;
            _fromPort = edge.FromPort;
            _toNodeId = edge.ToNodeId;
            _toPort = edge.ToPort;
        }

        public bool Matches(GraphEdge edge) =>
            string.Equals(_fromNodeId, edge.FromNodeId, StringComparison.Ordinal)
            && string.Equals(_fromPort, edge.FromPort, StringComparison.Ordinal)
            && string.Equals(_toNodeId, edge.ToNodeId, StringComparison.Ordinal)
            && string.Equals(_toPort, edge.ToPort, StringComparison.Ordinal);
    }

    private readonly struct StageStamp
    {
        private readonly string _id;
        private readonly string _displayName;
        private readonly string _description;
        private readonly string _category;
        private readonly NodeType _appliesTo;
        private readonly InputStamp[] _inputs;
        private readonly OutputStamp[] _outputs;
        private readonly GraphStamp _graph;

        private StageStamp(
            string id,
            string displayName,
            string description,
            string category,
            NodeType appliesTo,
            InputStamp[] inputs,
            OutputStamp[] outputs,
            GraphStamp graph)
        {
            _id = id;
            _displayName = displayName;
            _description = description;
            _category = category;
            _appliesTo = appliesTo;
            _inputs = inputs;
            _outputs = outputs;
            _graph = graph;
        }

        public static StageStamp Capture(CompositeStageDefinition stage)
        {
            var inputs = new InputStamp[stage.Inputs.Count];
            for (var i = 0; i < inputs.Length; i++)
            {
                inputs[i] = new InputStamp(stage.Inputs[i]);
            }

            var outputs = new OutputStamp[stage.Outputs.Count];
            for (var i = 0; i < outputs.Length; i++)
            {
                outputs[i] = new OutputStamp(stage.Outputs[i]);
            }

            return new StageStamp(
                stage.Id,
                stage.DisplayName,
                stage.Description,
                stage.Category,
                stage.AppliesTo,
                inputs,
                outputs,
                GraphStamp.Capture(stage.Graph));
        }

        public bool Matches(CompositeStageDefinition stage)
        {
            if (!string.Equals(_id, stage.Id, StringComparison.Ordinal)
                || !string.Equals(_displayName, stage.DisplayName, StringComparison.Ordinal)
                || !string.Equals(_description, stage.Description, StringComparison.Ordinal)
                || !string.Equals(_category, stage.Category, StringComparison.Ordinal)
                || _appliesTo != stage.AppliesTo
                || stage.Inputs.Count != _inputs.Length
                || stage.Outputs.Count != _outputs.Length
                || stage.Graph is null
                || !_graph.Matches(stage.Graph))
            {
                return false;
            }

            for (var i = 0; i < _inputs.Length; i++)
            {
                if (!_inputs[i].Matches(stage.Inputs[i]))
                {
                    return false;
                }
            }

            for (var i = 0; i < _outputs.Length; i++)
            {
                if (!_outputs[i].Matches(stage.Outputs[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private readonly struct InputStamp
    {
        private readonly string _name;
        private readonly GraphValueKind _kind;
        private readonly bool _isRequired;
        private readonly bool _isMulti;
        private readonly string _innerTarget;

        public InputStamp(CompositePortBinding input)
        {
            _name = input.Name;
            _kind = input.Kind;
            _isRequired = input.IsRequired;
            _isMulti = input.IsMulti;
            _innerTarget = input.InnerTarget;
        }

        public bool Matches(CompositePortBinding input) =>
            string.Equals(_name, input.Name, StringComparison.Ordinal)
            && _kind == input.Kind
            && _isRequired == input.IsRequired
            && _isMulti == input.IsMulti
            && string.Equals(_innerTarget, input.InnerTarget, StringComparison.Ordinal);
    }

    private readonly struct OutputStamp
    {
        private readonly string _name;
        private readonly GraphValueKind _kind;
        private readonly string _innerSource;

        public OutputStamp(CompositeOutputBinding output)
        {
            _name = output.Name;
            _kind = output.Kind;
            _innerSource = output.InnerSource;
        }

        public bool Matches(CompositeOutputBinding output) =>
            string.Equals(_name, output.Name, StringComparison.Ordinal)
            && _kind == output.Kind
            && string.Equals(_innerSource, output.InnerSource, StringComparison.Ordinal);
    }
}
