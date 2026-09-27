using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;

public sealed class AdmonitionIconOptions
{
    private static readonly IReadOnlyDictionary<string, string> DefaultIcons =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["note"] = "&#xe88f;",
            ["tip"] = "&#xe0f0;",
            ["warning"] = "&#xe002;",
            ["error"] = "&#xe5cd;",

            ["question"] = "&#xe887;",
            ["abstract"] = "&#xe8d2;",
            ["example"] = "&#xe86f;",
            ["quote"] = "&#xe244;",

            ["info"] = "&#xe88f;",
            ["success"] = "&#xe86c;"
        };

    private readonly object _gate = new();
    private IReadOnlyDictionary<string, string> _icons;
    private int _revision;

    public AdmonitionIconOptions()
    {
        _icons = new Dictionary<string, string>(DefaultIcons, StringComparer.Ordinal);
    }

    /// <summary>
    /// 图标配置版本：每次 <see cref="Configure"/> 后递增，渲染缓存据此失效。
    /// </summary>
    public int Revision => Volatile.Read(ref _revision);

    public string GetIcon(string kind)
    {
        var icons = Volatile.Read(ref _icons);
        return icons.TryGetValue(kind, out var icon)
            ? icon
            : icons[AdmonitionCatalog.DefaultAdmonitionKind];
    }

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

        var updated = new Dictionary<string, string>(_icons, StringComparer.Ordinal);
        foreach (var pair in icons)
            updated[pair.Key] = pair.Value;

        lock (_gate)
        {
            Volatile.Write(ref _icons, updated);
            Interlocked.Increment(ref _revision);
        }
    }
}
