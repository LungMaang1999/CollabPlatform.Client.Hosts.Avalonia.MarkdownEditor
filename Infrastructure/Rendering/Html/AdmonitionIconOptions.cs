using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;

public sealed class AdmonitionIconOptions
{
    // 默认码点须与工具栏 MarkdownEditorToolbar.axaml 的 Material_Icons 保持一致
    // （已按 MaterialIcons-Regular.ttf cmap+GSUB 核对）。提示块用实心字形，
    // 自定义块同义项用 outline 字形区分，同名 kind 必须分两张表存放。
    private static readonly IReadOnlyDictionary<string, string> DefaultAdmonitionIcons =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["note"] = "&#xe88e;",      // info
            ["tip"] = "&#xe0f0;",       // lightbulb
            ["warning"] = "&#xe002;",   // warning
            ["error"] = "&#xe000;",     // error
            ["question"] = "&#xe887;",  // help
            ["abstract"] = "&#xe873;",  // description
            ["example"] = "&#xea4b;",   // science
            ["quote"] = "&#xe244;"      // format_quote
        };

    private static readonly IReadOnlyDictionary<string, string> DefaultCustomBlockIcons =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["info"] = "&#xe88f;",      // info_outline
            ["success"] = "&#xe92d;",   // check_circle_outline
            ["warning"] = "&#xe002;",   // warning（与提示块同字形）
            ["error"] = "&#xe001;",     // error_outline
            ["question"] = "&#xe8fd;",  // help_outline
            ["abstract"] = "&#xe26c;",  // notes
            ["example"] = "&#xea4b;",   // science
            ["quote"] = "&#xe244;"      // format_quote
        };

    private readonly object _gate = new();
    private IReadOnlyDictionary<string, string> _admonitionIcons;
    private IReadOnlyDictionary<string, string> _customBlockIcons;
    private int _revision;

    public AdmonitionIconOptions()
    {
        _admonitionIcons = new Dictionary<string, string>(DefaultAdmonitionIcons, StringComparer.Ordinal);
        _customBlockIcons = new Dictionary<string, string>(DefaultCustomBlockIcons, StringComparer.Ordinal);
    }

    /// <summary>
    /// 图标配置版本：每次 <see cref="Configure"/> 后递增，渲染缓存据此失效。
    /// </summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>
    /// 获取 Admonition 提示块图标（实心字形体系）。
    /// </summary>
    public string GetAdmonitionIcon(string kind)
    {
        var icons = Volatile.Read(ref _admonitionIcons);
        return icons.TryGetValue(kind, out var icon)
            ? icon
            : icons[AdmonitionCatalog.DefaultAdmonitionKind];
    }

    /// <summary>
    /// 获取自定义引用块图标（outline 字形体系）。
    /// </summary>
    public string GetCustomBlockIcon(string kind)
    {
        var icons = Volatile.Read(ref _customBlockIcons);
        return icons.TryGetValue(kind, out var icon)
            ? icon
            : icons[AdmonitionCatalog.DefaultCustomBlockKind];
    }

    /// <summary>
    /// 兼容旧调用方：按提示块图标体系取码点。
    /// </summary>
    public string GetIcon(string kind) => GetAdmonitionIcon(kind);

    public void Configure(IReadOnlyDictionary<string, string> icons)
    {
        ArgumentNullException.ThrowIfNull(icons);

        foreach (var pair in icons)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                throw new ArgumentException(
                    "图标配置的类别键与码点值均不能为空。", nameof(icons));
            }
        }

        // 覆盖项按 key 命中的体系生效：仅存在于一张表的 kind（如 note/info）
        // 只改对应体系；同名 kind（如 error）显式覆盖时两套体系统一为指定码点。
        var updatedAdmonition = new Dictionary<string, string>(
            Volatile.Read(ref _admonitionIcons), StringComparer.Ordinal);
        var updatedCustom = new Dictionary<string, string>(
            Volatile.Read(ref _customBlockIcons), StringComparer.Ordinal);

        foreach (var pair in icons)
        {
            var inAdmonition = updatedAdmonition.ContainsKey(pair.Key);
            var inCustom = updatedCustom.ContainsKey(pair.Key);

            if (inAdmonition)
                updatedAdmonition[pair.Key] = pair.Value;
            if (inCustom)
                updatedCustom[pair.Key] = pair.Value;

            // 两张表都没有的自定义 kind：两体系都登记，保持可扩展语义。
            if (!inAdmonition && !inCustom)
            {
                updatedAdmonition[pair.Key] = pair.Value;
                updatedCustom[pair.Key] = pair.Value;
            }
        }

        lock (_gate)
        {
            Volatile.Write(ref _admonitionIcons, updatedAdmonition);
            Volatile.Write(ref _customBlockIcons, updatedCustom);
            Interlocked.Increment(ref _revision);
        }
    }
}
