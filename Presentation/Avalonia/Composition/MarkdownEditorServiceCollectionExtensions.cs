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
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.Services;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Presentation.Avalonia.Composition;

public static class MarkdownEditorServiceCollectionExtensions
{
    /// <summary>  
    /// Registers all Markdown Editor domain, application, infrastructure, and presentation services.  
    /// </summary>  
    public static IServiceCollection AddMarkdownEditor(
        this IServiceCollection services,
        Action<MarkdownParserOptions>? configureParser = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var parserOptions = new MarkdownParserOptions();
        configureParser?.Invoke(parserOptions);

        services.AddSingleton(parserOptions);

        // Core parsing, persistence & serializer  
        services.AddSingleton<IMarkdownParser, MarkdownParser>();
        services.AddSingleton<IDocumentSerializer, DocumentSerializer>();
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<IDocumentService, DocumentService>();

        // Text slice editing & mutation applier  
        services.AddTransient<IMarkdownSourceEditor, MarkdownSourceEditor>();
        services.AddTransient<IMarkdownEditApplier, MarkdownEditApplier>();

        // Rendering factory  
        services.AddTransient<IDocumentRendererFactory, HtmlRendererFactory>();

        // ViewModels  
        services.AddTransient<EditorViewModel>();

        services.AddSingleton<IUiThreadDispatcher, SynchronizationContextUiThreadDispatcher>();
        services.AddSingleton<IFileWatcherService, FileWatcherService>();
        return services;
    }
}