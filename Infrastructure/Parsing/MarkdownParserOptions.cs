namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

public sealed class MarkdownParserOptions
{
    public bool EnablePipeTables { get; set; } = true;
    public bool EnableTaskLists { get; set; } = true;
    public bool EnableFootnotes { get; set; } = true;
    public bool EnableYamlFrontMatter { get; set; } = true;
    public bool EnableEmphasisExtras { get; set; } = true;
    public bool EnableAutoLinks { get; set; } = true;
}