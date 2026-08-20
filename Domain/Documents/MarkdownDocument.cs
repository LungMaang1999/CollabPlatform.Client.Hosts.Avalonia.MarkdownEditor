using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Validation;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;

/// <summary>
/// 核心聚合根：表示包含 AST 树、样式表、元数据和编辑器状态的 Markdown 文档。
/// </summary>
public sealed class MarkdownDocument
{
    private long _sourceRevision;
    private long _savedSourceRevision;
    private long _styleRevision;
    private long _savedStyleRevision;

    private string _sourceMarkdown = string.Empty;
    private string _savedSourceMarkdown = string.Empty;
    private string _savedStyleFingerprint = string.Empty;

    private MarkdownNode _root;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = string.Empty;

    public string SourceMarkdown => _sourceMarkdown;
    public MarkdownNode Root => _root;
    public StyleSheet StyleSheet { get; private set; } = new();
    public DocumentMetadata Metadata { get; private set; } = new();
    public EditorState EditorState { get; private set; } = new();

    public long SourceRevision => _sourceRevision;
    public long SavedSourceRevision => _savedSourceRevision;
    public long StyleRevision => _styleRevision;
    public long SavedStyleRevision => _savedStyleRevision;

    public long Revision => Math.Max(_sourceRevision, _styleRevision);
    public long SavedRevision => Math.Max(_savedSourceRevision, _savedStyleRevision);

    public bool IsSourceModified => _sourceRevision != _savedSourceRevision ||
                                   !string.Equals(_sourceMarkdown, _savedSourceMarkdown, StringComparison.Ordinal);

    public bool IsStyleModified => _styleRevision != _savedStyleRevision;

    public bool IsModified => IsSourceModified || IsStyleModified;

    public MarkdownDocument()
    {
        _root = CreateDefaultRoot();
        _savedStyleFingerprint = CreateStyleFingerprint();
    }

    public MarkdownNode? FindNode(string id) =>
        string.IsNullOrWhiteSpace(id) ? null : _root.FindById(id);

    public void Validate()
    {
        var error = GetValidationIssues()
            .FirstOrDefault(issue => issue.Severity == DocumentValidationSeverity.Error);

        if (error is not null)
            throw new DocumentValidationException($"[{error.Code}] {error.Message}");
    }

    public IReadOnlyList<DocumentValidationIssue> GetValidationIssues()
    {
        var issues = new List<DocumentValidationIssue>();

        if (Root is null)
        {
            issues.Add(new DocumentValidationIssue(DocumentValidationSeverity.Error, "NULL_ROOT", "Document root cannot be null."));
            return issues;
        }

        if (Root.Type != NodeType.Document)
            issues.Add(new DocumentValidationIssue(DocumentValidationSeverity.Error, "INVALID_ROOT_TYPE", "Document root must have Document node type.", Root.Id));

        if (!Root.IsSynthetic)
            issues.Add(new DocumentValidationIssue(DocumentValidationSeverity.Error, "INVALID_ROOT_SYNTHETIC_STATE", "Document root must be synthetic.", Root.Id));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        ValidateNode(Root, expectedParent: null, ids, issues);

        return issues;
    }

    public bool UpdateSourceMarkdown(string? source)
    {
        source ??= string.Empty;
        if (string.Equals(_sourceMarkdown, source, StringComparison.Ordinal))
            return false;

        _sourceMarkdown = source;
        MarkSourceChanged();
        return true;
    }

    internal void ReplaceParsedRoot(MarkdownNode root, string? source, bool markChanged)
    {
        ArgumentNullException.ThrowIfNull(root);
        source ??= string.Empty;

        var sourceChanged = !string.Equals(_sourceMarkdown, source, StringComparison.Ordinal);

        _root = root;
        _sourceMarkdown = source;

        NormalizeEditorState();

        if (markChanged && sourceChanged)
            MarkSourceChanged();
    }

    internal void RestoreLoadedState(
        string? source,
        MarkdownNode root,
        StyleSheet styleSheet,
        DocumentMetadata metadata,
        EditorState editorState,
        string? id,
        string? filePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(styleSheet);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(editorState);

        _sourceMarkdown = source ?? string.Empty;
        _root = root;
        StyleSheet = styleSheet;
        Metadata = metadata;
        EditorState = editorState;

        if (!string.IsNullOrWhiteSpace(id)) Id = id;
        if (!string.IsNullOrWhiteSpace(filePath)) FilePath = filePath;

        MarkSaved();
    }

    public void MarkSourceChanged()
    {
        Interlocked.Increment(ref _sourceRevision);
        Metadata.ModifiedUtc = DateTime.UtcNow;
    }

    public void MarkStyleChanged()
    {
        Interlocked.Increment(ref _styleRevision);
        Metadata.ModifiedUtc = DateTime.UtcNow;
    }

    public void MarkChanged() => MarkSourceChanged();

    public void MarkSaved()
    {
        _savedSourceRevision = _sourceRevision;
        _savedStyleRevision = _styleRevision;
        _savedSourceMarkdown = _sourceMarkdown;
        _savedStyleFingerprint = CreateStyleFingerprint();
    }

    public void NormalizeEditorState()
    {
        if (!string.IsNullOrWhiteSpace(EditorState.SelectedNodeId) && FindNode(EditorState.SelectedNodeId) is null)
            EditorState.SelectedNodeId = null;

        EditorState.ExpandedNodeIds.RemoveWhere(nodeId => FindNode(nodeId) is null);
        EditorState.CaretOffset = Math.Clamp(EditorState.CaretOffset, 0, SourceMarkdown.Length);
        EditorState.SelectionLength = Math.Clamp(EditorState.SelectionLength, 0, SourceMarkdown.Length - EditorState.CaretOffset);
    }

    private static void ValidateNode(MarkdownNode node, MarkdownNode? expectedParent, ISet<string> ids, ICollection<DocumentValidationIssue> issues)
    {
        if (!ReferenceEquals(node.Parent, expectedParent))
            issues.Add(new DocumentValidationIssue(DocumentValidationSeverity.Error, "INVALID_PARENT", "Node has an invalid parent reference.", node.Id));

        if (string.IsNullOrWhiteSpace(node.Id))
            issues.Add(new DocumentValidationIssue(DocumentValidationSeverity.Error, "EMPTY_NODE_ID", "Document node Id cannot be empty."));
        else if (!ids.Add(node.Id))
            issues.Add(new DocumentValidationIssue(DocumentValidationSeverity.Error, "DUPLICATE_NODE_ID", $"Duplicate node Id '{node.Id}'.", node.Id));

        foreach (var child in node.Children)
            ValidateNode(child, expectedParent: node, ids, issues);
    }

    public string CreateStyleFingerprint()
    {
        var builder = new StringBuilder(1024);

        // 1. 样式表快速序列化
        builder.Append("STYLESHEET:");
        foreach (var def in StyleSheet.Styles.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            builder.Append($"[ID={def.Id};P={def.ParentStyleId};A={def.AppliesTo};S=");
            AppendNodeStyleFast(builder, def.Style);
            builder.Append(']');
        }
        builder.Append('|');

        // 2. 定制节点样式与属性
        builder.Append("NODES:");
        foreach (var candidate in EnumerateSelfAndDescendants(_root))
        {
            if (!string.IsNullOrWhiteSpace(candidate.StyleId) || !IsDefaultStyle(candidate.LocalStyle) || candidate.Attributes.Count > 0)
            {
                builder.Append($"[NID={candidate.Id};SID={candidate.StyleId};S=");
                AppendNodeStyleFast(builder, candidate.LocalStyle);
                builder.Append(";ATTRS=");
                foreach (var attr in candidate.Attributes.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    builder.Append($"{attr.Key}={attr.Value};");
                }
                builder.Append(']');
            }
        }
        builder.Append('|');

        // 3. 元数据
        builder.Append($"META:{Metadata.Title}|{Metadata.Author}|{Metadata.Language}|{Metadata.Encoding}|");
        foreach (var property in Metadata.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
            builder.Append($"{property.Key}={property.Value};");
        builder.Append('|');

        // 4. 编辑器状态
        builder.Append($"STATE:{EditorState.SelectedNodeId}|{EditorState.CaretOffset}|{EditorState.SelectionLength}|");
        foreach (var nodeId in EditorState.ExpandedNodeIds.OrderBy(v => v, StringComparer.Ordinal))
            builder.Append($"{nodeId};");

        return ComputeSha256(builder.ToString());
    }

    private static void AppendNodeStyleFast(StringBuilder sb, NodeStyle? s)
    {
        if (s is null) return;
        sb.Append($"{s.FontFamily};{s.FontSize};{s.ForegroundColor};{s.BackgroundColor};{s.Bold};{s.Italic};{s.LineHeight};{s.TextAlign};{s.BorderColor};{s.BorderWidth};{s.CustomCss};");
        if (s.Margin is not null) sb.Append($"M:{s.Margin.Left},{s.Margin.Top},{s.Margin.Right},{s.Margin.Bottom};");
        if (s.Padding is not null) sb.Append($"P:{s.Padding.Left},{s.Padding.Top},{s.Padding.Right},{s.Padding.Bottom};");
    }

    private static bool IsDefaultStyle(NodeStyle? style) =>
        style is null || (style.FontSize is null && style.FontFamily is null && style.ForegroundColor is null &&
        style.BackgroundColor is null && style.Bold is null && style.Italic is null &&
        style.LineHeight is null && style.TextAlign is null && style.Margin is null &&
        style.Padding is null && style.BorderColor is null && (style.BorderWidth is null or 0) &&
        string.IsNullOrWhiteSpace(style.CustomCss));

    private static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static MarkdownNode CreateDefaultRoot() => new()
    {
        Type = NodeType.Document,
        Category = NodeCategory.Synthetic,
        IsSynthetic = true,
        Text = "Document"
    };

    private static IEnumerable<MarkdownNode> EnumerateSelfAndDescendants(MarkdownNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in EnumerateSelfAndDescendants(child))
                yield return descendant;
    }
}