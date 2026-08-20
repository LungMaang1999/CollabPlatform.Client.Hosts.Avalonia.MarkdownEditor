namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

public sealed class MarkdownParserOptions
{
    public bool EnablePipeTables { get; init; } = true;
    public bool EnableTaskLists { get; init; } = true;
    public bool EnableFootnotes { get; init; } = true;
    public bool EnableYamlFrontMatter { get; init; } = true;
}