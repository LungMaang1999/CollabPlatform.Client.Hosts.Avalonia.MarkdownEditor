using System;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Editing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Parsing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Threading;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.Services;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.Composition;

public static class MarkdownEditorServiceCollectionExtensions
{
    public static IServiceCollection AddMarkdownEditor(
        this IServiceCollection services,
        Action<MarkdownEditorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var editorOptions = new MarkdownEditorOptions();
        configure?.Invoke(editorOptions);

        // 同一实例同时按编辑器配置与解析器配置注册：MarkdownParser 依赖基类型。
        services.AddSingleton(editorOptions);
        services.AddSingleton<MarkdownParserOptions>(editorOptions);

        services.AddSingleton<IMarkdownParser, MarkdownParser>();
        services.AddSingleton<IDocumentSerializer, DocumentSerializer>();
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<IDocumentService, DocumentService>();

        services.AddTransient<IMarkdownSourceEditor, MarkdownSourceEditor>();
        services.AddTransient<IMarkdownEditApplier, MarkdownEditApplier>();

        services.AddTransient<IDocumentRendererFactory, HtmlRendererFactory>();
        services.AddSingleton<IStylePreviewRenderer, StylePreviewRenderer>();

        services.AddSingleton<IUiThreadDispatcher, AvaloniaUiThreadDispatcher>();
        services.AddSingleton<IFileWatcherService, FileWatcherService>();

        services.AddTransient<EditorViewModel>();

        return services;
    }
}
