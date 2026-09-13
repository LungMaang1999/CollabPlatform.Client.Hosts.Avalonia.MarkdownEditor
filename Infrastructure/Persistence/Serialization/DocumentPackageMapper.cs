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
                Id = string.IsNullOrWhiteSpace(document.Id) ? Guid.NewGuid().ToString("N") : document.Id,
                FileName = sourceFileName,
                Source = new SourceDto
                {
                    Path = sourceFileName,
                    Hash = sourceFileHash,
                    Encoding = "utf-8"
                },
                EditorState = new EditorStateDto
                {
                    SelectedNodeId = document.EditorState?.SelectedNodeId,
                    CaretOffset = document.EditorState?.CaretOffset ?? 0,
                    SelectionLength = document.EditorState?.SelectionLength ?? 0
                },
                Metadata = new MetadataDto
                {
                    Title = document.Metadata?.Title ?? string.Empty,
                    Author = document.Metadata?.Author ?? string.Empty,
                    Language = document.Metadata?.Language ?? string.Empty,
                    Encoding = document.Metadata?.Encoding ?? "utf-8",
                    CreatedUtc = document.Metadata?.CreatedUtc ?? DateTime.UtcNow,
                    ModifiedUtc = document.Metadata?.ModifiedUtc ?? DateTime.UtcNow
                }
            }
        };

        if (document.StyleSheet?.Styles is not null)
        {
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
        }

        if (document.EditorState?.ExpandedNodeIds is not null)
        {
            foreach (var id in document.EditorState.ExpandedNodeIds)
                dto.Document.EditorState.ExpandedNodeIds.Add(id);
        }

        if (document.Metadata?.Properties is not null)
        {
            foreach (var kv in document.Metadata.Properties)
                dto.Document.Metadata.Properties[kv.Key] = kv.Value;
        }

        if (document.Root is not null)
            dto.Document.Root = ToNodeDto(document.Root);

        return dto;

        static NodeDto ToNodeDto(MarkdownNode node)
        {
            var dto = new NodeDto
            {
                Id = string.IsNullOrWhiteSpace(node.Id) ? Guid.NewGuid().ToString("N") : node.Id,
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

            if (node.Attributes is not null)
            {
                foreach (var kv in node.Attributes) dto.Attributes[kv.Key] = kv.Value;
            }

            if (node.Children is not null)
            {
                foreach (var c in node.Children) dto.Children.Add(ToNodeDto(c));
            }

            return dto;
        }
    }

    /// <summary>
    /// 将 NodeStyle 转换为 NodeStyleDto
    /// </summary>
    public static NodeStyleDto ToNodeStyleDto(NodeStyle? s)
    {
        if (s is null) return new NodeStyleDto();

        return new NodeStyleDto
        {
            FontFamily = s.FontFamily,
            FontSize = s.FontSize,
            ForegroundColor = s.ForegroundColor,
            BackgroundColor = s.BackgroundColor,
            Bold = s.Bold,
            Italic = s.Italic,
            LineHeight = s.LineHeight,
            TextAlign = s.TextAlign,
            BorderColor = s.BorderColor,
            BorderWidth = s.BorderWidth,
            CustomCss = s.CustomCss,
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
            }
        };
    }

    /// <summary>
    /// 将 ComputedStyle 转换为 NodeStyleDto（支持运行时计算样式序列化/传递）
    /// </summary>
    public static NodeStyleDto ToNodeStyleDto(ComputedStyle? s)
    {
        if (s is null) return new NodeStyleDto();

        return new NodeStyleDto
        {
            FontFamily = s.FontFamily,
            FontSize = s.FontSize,
            ForegroundColor = s.ForegroundColor,
            BackgroundColor = s.BackgroundColor,
            Bold = s.Bold,
            Italic = s.Italic,
            LineHeight = s.LineHeight,
            TextAlign = s.TextAlign,
            BorderColor = s.BorderColor,
            BorderWidth = s.BorderWidth,
            CustomCss = s.CustomCss,
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
            }
        };
    }

    /// <summary>
    /// 将 NodeStyleDto 转换为 NodeStyle 领域模型
    /// </summary>
    public static NodeStyle ToNodeStyleDomain(NodeStyleDto? dto)
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
        if (docDto.Styles is not null)
        {
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
        }

        var editorState = new EditorState();
        if (docDto.EditorState is not null)
        {
            editorState.SelectedNodeId = docDto.EditorState.SelectedNodeId;
            editorState.CaretOffset = docDto.EditorState.CaretOffset;
            editorState.SelectionLength = docDto.EditorState.SelectionLength;
            if (docDto.EditorState.ExpandedNodeIds is not null)
            {
                foreach (var id in docDto.EditorState.ExpandedNodeIds) editorState.ExpandedNodeIds.Add(id);
            }
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

            if (docDto.Metadata.Properties is not null)
            {
                foreach (var kv in docDto.Metadata.Properties) metadata.Properties[kv.Key] = kv.Value;
            }
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

        // 使用领域原生方法 RestoreLoadedState 恢复整颗 AST 状态
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
            if (dto.Attributes is not null)
            {
                foreach (var kv in dto.Attributes) node.Attributes[kv.Key] = kv.Value;
            }

            if (dto.Children is not null)
            {
                foreach (var c in dto.Children) node.Children.Add(ToNodeDomain(c));
            }

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