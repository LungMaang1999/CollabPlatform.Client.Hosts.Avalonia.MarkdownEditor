namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

public static class AdmonitionCatalog
{
    public const string DefaultAdmonitionKind = "note";
    public const string DefaultCustomBlockKind = "info";

    public static readonly IReadOnlySet<string> AdmonitionKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "note", "tip", "warning", "error",
        "question", "abstract", "example", "quote"
    };

    public static readonly IReadOnlySet<string> CustomBlockKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "info", "success", "warning", "error",
        "question", "abstract", "example", "quote"
    };

    private static readonly Dictionary<string, string> AdmonitionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NOTE"] = "note",
        ["INFO"] = "note",
        ["NOTICE"] = "note",

        ["TIP"] = "tip",
        ["HINT"] = "tip",
        ["SUCCESS"] = "tip",

        ["WARNING"] = "warning",
        ["IMPORTANT"] = "warning",

        ["ERROR"] = "error",
        ["DANGER"] = "error",
        ["CAUTION"] = "error",
        ["CRITICAL"] = "error",

        ["QUESTION"] = "question",
        ["FAQ"] = "question",
        ["HELP"] = "question",

        ["ABSTRACT"] = "abstract",
        ["SUMMARY"] = "abstract",
        ["EXCERPT"] = "abstract",

        ["EXAMPLE"] = "example",
        ["DEMO"] = "example",
        ["SNIPPET"] = "example",

        ["QUOTE"] = "quote",
        ["CITE"] = "quote"
    };

    private static readonly Dictionary<string, string> CustomBlockAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["INFO"] = "info",
        ["NOTE"] = "info",
        ["NOTICE"] = "info",

        ["SUCCESS"] = "success",
        ["TIP"] = "success",
        ["HINT"] = "success",

        ["WARNING"] = "warning",
        ["WARN"] = "warning",
        ["IMPORTANT"] = "warning",

        ["ERROR"] = "error",
        ["DANGER"] = "error",
        ["CRITICAL"] = "error",

        ["QUESTION"] = "question",
        ["FAQ"] = "question",
        ["HELP"] = "question",

        ["ABSTRACT"] = "abstract",
        ["SUMMARY"] = "abstract",
        ["EXCERPT"] = "abstract",

        ["EXAMPLE"] = "example",
        ["DEMO"] = "example",
        ["SNIPPET"] = "example",

        ["QUOTE"] = "quote",
        ["CITE"] = "quote"
    };

    private static readonly Dictionary<string, string> DefaultTitles = new(StringComparer.Ordinal)
    {
        ["note"] = "信息",
        ["tip"] = "提示",
        ["warning"] = "警告",
        ["error"] = "错误",
        ["question"] = "疑问",
        ["abstract"] = "摘要",
        ["example"] = "示例",
        ["quote"] = "引用"
    };

    public static bool TryMapAdmonitionLabel(string? label, out string kind) =>
        TryMapLabel(AdmonitionAliases, label, out kind);

    public static bool TryMapCustomBlockLabel(string? label, out string kind) =>
        TryMapLabel(CustomBlockAliases, label, out kind);

    public static string GetDefaultTitle(string kind) =>
        DefaultTitles.TryGetValue(kind, out var title) ? title : DefaultTitles[DefaultAdmonitionKind];

    private static bool TryMapLabel(Dictionary<string, string> aliases, string? label, out string kind)
    {
        if (!string.IsNullOrWhiteSpace(label) && aliases.TryGetValue(label, out var mapped))
        {
            kind = mapped;
            return true;
        }

        kind = string.Empty;
        return false;
    }
}
