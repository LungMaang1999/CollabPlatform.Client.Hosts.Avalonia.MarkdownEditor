using System;
using System.Linq;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

internal static class DocumentPackageMapper
{
    public static DocumentPackageDto ToDto(MarkdownDocument document, string sourceFileName, string sourceFileHash)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(sourceFileName)) throw new ArgumentException("sourceFileName cannot be empty.", nameof(sourceFileName));
        if (string.IsNullOrWhiteSpace(sourceFileHash)) throw new ArgumentException("sourceFileHash cannot be empty.", nameof(sourceFileHash));

        var dto = new DocumentPackageDto
        {
            Document = new DocumentDto
            {
                Id = document.Id,
                FileName = sourceFileName,
                Source = new SourceDto
                {
                    Path = sourceFileName,
                    Hash = sourceFileHash,
                    Encoding = "utf-8"
                },
                EditorState = new EditorStateDto
                {
                    SelectedNodeId = document.EditorState.SelectedNodeId,
                    CaretOffset = document.EditorState.CaretOffset,
                    SelectionLength = document.EditorState.SelectionLength
                },
                Metadata = new MetadataDto
                {
                    Title = document.Metadata.Title,
                    Author = document.Metadata.Author,
                    Language = document.Metadata.Language,
                    Encoding = document.Metadata.Encoding,
                    CreatedUtc = document.Metadata.CreatedUtc,
                    ModifiedUtc = document.Metadata.ModifiedUtc
                }
            }
        };

        foreach (var sd in document.StyleSheet.Styles)
        {
            dto.Document.Styles.Add(new StyleDefinitionDto
            {
                Id = sd.Id,
                Name = sd.Name,
                ParentStyleId = sd.ParentStyleId,
                AppliesTo = sd.AppliesTo?.ToString(),
                Style = ToNodeStyleDto(sd.Style)
            });
        }

        foreach (var id in document.EditorState.ExpandedNodeIds)
            dto.Document.EditorState.ExpandedNodeIds.Add(id);

        foreach (var kv in document.Metadata.Properties)
            dto.Document.Metadata.Properties[kv.Key] = kv.Value;

        if (document.Root is not null)
            dto.Document.Root = ToNodeDto(document.Root);

        return dto;

        static NodeDto ToNodeDto(MarkdownNode node)
        {
            var dto = new NodeDto
            {
                Id = node.Id,
                Type = node.Type.ToString(),
                Category = node.Category.ToString(),
                Text = node.Text ?? string.Empty,
                RawMarkdown = node.RawMarkdown ?? string.Empty,
                Level = node.Level,
                StyleId = node.StyleId,
                IsTableHeader = node.IsTableHeader,
                IsSynthetic = node.IsSynthetic,
                LocalStyle = ToNodeStyleDto(node.LocalStyle)
            };

            foreach (var kv in node.Attributes) dto.Attributes[kv.Key] = kv.Value;
            foreach (var c in node.Children) dto.Children.Add(ToNodeDto(c));
            return dto;
        }

        static NodeStyleDto ToNodeStyleDto(NodeStyle s) =>
            s is null ? new NodeStyleDto() : new NodeStyleDto
            {
                FontFamily = s.FontFamily,
                FontSize = s.FontSize,
                ForegroundColor = s.ForegroundColor,
                BackgroundColor = s.BackgroundColor,
                Bold = s.Bold,
                Italic = s.Italic,
                LineHeight = s.LineHeight,
                TextAlign = s.TextAlign,
                Margin = s.Margin is null ? null : new ThicknessValueDto
                {
                    Left = s.Margin.Left,
                    Top = s.Margin.Top,
                    Right = s.Margin.Right,
                    Bottom = s.Margin.Bottom
                },
                Padding = s.Padding is null ? null : new ThicknessValueDto
                {
                    Left = s.Padding.Left,
                    Top = s.Padding.Top,
                    Right = s.Padding.Right,
                    Bottom = s.Padding.Bottom
                },
                BorderColor = s.BorderColor,
                BorderWidth = s.BorderWidth,
                CustomCss = s.CustomCss
            };
    }

    public static MarkdownDocument ToDomain(DocumentPackageDto dto, string sourceMarkdown, string sourceFilePath)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var docDto = dto.Document ?? new DocumentDto();

        var document = new MarkdownDocument
        {
            Id = string.IsNullOrWhiteSpace(docDto.Id) ? Guid.NewGuid().ToString("N") : docDto.Id,
            FilePath = string.IsNullOrEmpty(sourceFilePath) ? docDto.Source?.Path ?? string.Empty : sourceFilePath
        };

        var styleSheet = new StyleSheet();
        styleSheet.Styles.Clear();
        foreach (var sd in docDto.Styles)
        {
            var def = new StyleDefinition
            {
                Id = sd.Id,
                Name = sd.Name,
                ParentStyleId = sd.ParentStyleId,
                AppliesTo = TryParseNodeType(sd.AppliesTo),
                Style = ToNodeStyleDomain(sd.Style)
            };
            styleSheet.Styles.Add(def);
        }

        var editorState = new EditorState();
        if (docDto.EditorState is not null)
        {
            editorState.SelectedNodeId = docDto.EditorState.SelectedNodeId;
            editorState.CaretOffset = docDto.EditorState.CaretOffset;
            editorState.SelectionLength = docDto.EditorState.SelectionLength;
            foreach (var id in docDto.EditorState.ExpandedNodeIds) editorState.ExpandedNodeIds.Add(id);
        }

        var metadata = new DocumentMetadata();
        if (docDto.Metadata is not null)
        {
            metadata.Title = docDto.Metadata.Title;
            metadata.Author = docDto.Metadata.Author;
            metadata.Language = docDto.Metadata.Language;
            metadata.Encoding = docDto.Metadata.Encoding;
            metadata.CreatedUtc = docDto.Metadata.CreatedUtc;
            metadata.ModifiedUtc = docDto.Metadata.ModifiedUtc;

            foreach (var kv in docDto.Metadata.Properties) metadata.Properties[kv.Key] = kv.Value;
        }

        MarkdownNode root;
        if (docDto.Root is not null)
        {
            root = ToNodeDomain(docDto.Root);
            SetParents(root, null);
        }
        else
        {
            root = new MarkdownNode
            {
                Type = NodeType.Document,
                Category = NodeCategory.Synthetic,
                IsSynthetic = true,
                Text = "Document"
            };
        }

        // 使用领域原生方法 RestoreLoadedState 替换原有的反射私有字段 _root
        document.RestoreLoadedState(
            source: sourceMarkdown,
            root: root,
            styleSheet: styleSheet,
            metadata: metadata,
            editorState: editorState,
            id: document.Id,
            filePath: document.FilePath
        );

        return document;

        static NodeStyle ToNodeStyleDomain(NodeStyleDto dto)
        {
            if (dto is null) return new NodeStyle();

            var ns = new NodeStyle
            {
                FontFamily = dto.FontFamily,
                FontSize = dto.FontSize,
                ForegroundColor = dto.ForegroundColor,
                BackgroundColor = dto.BackgroundColor,
                Bold = dto.Bold,
                Italic = dto.Italic,
                LineHeight = dto.LineHeight,
                TextAlign = dto.TextAlign,
                BorderColor = dto.BorderColor,
                BorderWidth = dto.BorderWidth ?? 0,
                CustomCss = dto.CustomCss
            };

            if (dto.Margin is not null)
                ns.Margin = new ThicknessValue(dto.Margin.Left, dto.Margin.Top, dto.Margin.Right, dto.Margin.Bottom);

            if (dto.Padding is not null)
                ns.Padding = new ThicknessValue(dto.Padding.Left, dto.Padding.Top, dto.Padding.Right, dto.Padding.Bottom);

            return ns;
        }

        static MarkdownNode ToNodeDomain(NodeDto dto)
        {
            var node = new MarkdownNode
            {
                Id = string.IsNullOrWhiteSpace(dto.Id) ? Guid.NewGuid().ToString("N") : dto.Id,
                Type = TryParseNodeTypeEnum(dto.Type) ?? NodeType.Custom,
                Category = TryParseNodeCategory(dto.Category) ?? NodeCategory.Container,
                Text = dto.Text ?? string.Empty,
                RawMarkdown = dto.RawMarkdown ?? string.Empty,
                Level = dto.Level,
                IsTableHeader = dto.IsTableHeader,
                IsSynthetic = dto.IsSynthetic,
                LocalStyle = ToNodeStyleDomain(dto.LocalStyle),
                StyleId = dto.StyleId
            };

            node.Attributes.Clear();
            foreach (var kv in dto.Attributes) node.Attributes[kv.Key] = kv.Value;

            foreach (var c in dto.Children) node.Children.Add(ToNodeDomain(c));

            return node;
        }

        static void SetParents(MarkdownNode node, MarkdownNode? parent)
        {
            node.Parent = parent;
            foreach (var c in node.Children) SetParents(c, node);
        }

        static NodeType? TryParseNodeType(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return Enum.TryParse<NodeType>(s, out var v) ? v : null;
        }

        static NodeType? TryParseNodeTypeEnum(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return Enum.TryParse<NodeType>(s, out var v) ? v : (NodeType?)null;
        }

        static NodeCategory? TryParseNodeCategory(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return Enum.TryParse<NodeCategory>(s, out var v) ? v : null;
        }
    }
}