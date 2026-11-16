using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;

public sealed class AdmonitionIconOptions
{
    private readonly object _gate = new();
    private IconConfig _config = IconConfig.Default;

    /// <summary>
    /// 图标配置版本：每次 <see cref="Configure"/> 后递增。仅供变更观测，不进入块签名。
    /// </summary>
    public int Revision => Snapshot().Revision;

    /// <summary>
    /// 图标配置的内容指纹：两套字形表按 key 排序后折叠，只由最终配置决定，
    /// 与下发顺序、下发次数无关，可直接作为渲染缓存的失效键。
    /// </summary>
    public string Fingerprint => Snapshot().Fingerprint;

    /// <summary>
    /// 取一份不可变快照。一次渲染必须在开始时取一次，之后的指纹与全部字形
    /// 都从同一份快照读取，保证二者不会来自不同配置。
    /// </summary>
    public IconConfig Snapshot() => Volatile.Read(ref _config);

    /// <summary>
    /// 获取 Admonition 提示块图标（实心字形体系）。
    /// </summary>
    public string GetAdmonitionIcon(string kind) => Snapshot().GetAdmonitionIcon(kind);

    /// <summary>
    /// 获取自定义引用块图标（outline 字形体系）。
    /// </summary>
    public string GetCustomBlockIcon(string kind) => Snapshot().GetCustomBlockIcon(kind);

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

        lock (_gate)
        {
            var current = Volatile.Read(ref _config);

            // 覆盖项按 key 命中的体系生效：仅存在于一张表的 kind（如 note/info）
            // 只改对应体系；同名 kind（如 error）显式覆盖时两套体系统一为指定码点。
            var updatedAdmonition = new Dictionary<string, string>(
                current.AdmonitionIcons, StringComparer.Ordinal);
            var updatedCustom = new Dictionary<string, string>(
                current.CustomBlockIcons, StringComparer.Ordinal);

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

            // 字形表、指纹、修订号一次性发布，读取方只会看到自洽的整体。
            Volatile.Write(
                ref _config,
                IconConfig.Create(updatedAdmonition, updatedCustom, current.Revision + 1));
        }
    }

    /// <summary>
    /// 只配置自定义内容块（outline 字形体系）的 kind 图标。
    /// 与 <see cref="Configure"/> 分开是必要的：像 <c>note</c> 这类同名 kind 同时存在于
    /// 提示块表中，走 <see cref="Configure"/> 只会改提示块字形，自定义块拿不到自己的图标。
    /// 两套表仍共用同一个不可变 <see cref="IconConfig"/> 快照与内容指纹。
    /// </summary>
    public void ConfigureCustomBlockIcons(IReadOnlyDictionary<string, string> icons)
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

        lock (_gate)
        {
            var current = Volatile.Read(ref _config);
            var updatedCustom = new Dictionary<string, string>(
                current.CustomBlockIcons, StringComparer.Ordinal);

            foreach (var pair in icons)
            {
                updatedCustom[pair.Key] = pair.Value;
            }

            Volatile.Write(
                ref _config,
                IconConfig.Create(current.AdmonitionIcons, updatedCustom, current.Revision + 1));
        }
    }

    /// <summary>
    /// 捕获当前字形表与修订号。用于调用方（典型是测试）恢复全局配置，
    /// 避免进程级静态实例把状态泄漏给后续用例。
    /// </summary>
    public AdmonitionIconState Capture()
    {
        var config = Snapshot();
        return new AdmonitionIconState(config.AdmonitionIcons, config.CustomBlockIcons, config.Revision);
    }

    /// <summary>
    /// 恢复到 <see cref="Capture"/> 时的字形表与修订号，指纹据恢复后的表重新计算。
    /// </summary>
    public void Restore(AdmonitionIconState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_gate)
        {
            Volatile.Write(
                ref _config,
                IconConfig.Create(state.AdmonitionIcons, state.CustomBlockIcons, state.Revision));
        }
    }
}

/// <summary>
/// 不可变图标配置快照：两套字形表、内容指纹与修订号作为一个整体发布。
/// </summary>
public sealed record IconConfig(
    IReadOnlyDictionary<string, string> AdmonitionIcons,
    IReadOnlyDictionary<string, string> CustomBlockIcons,
    string Fingerprint,
    int Revision)
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

    internal static readonly IconConfig Default =
        Create(DefaultAdmonitionIcons, DefaultCustomBlockIcons, revision: 0);

    internal static IconConfig Create(
        IReadOnlyDictionary<string, string> admonition,
        IReadOnlyDictionary<string, string> custom,
        int revision)
    {
        var frozenAdmonition = new Dictionary<string, string>(admonition, StringComparer.Ordinal);
        var frozenCustom = new Dictionary<string, string>(custom, StringComparer.Ordinal);
        return new IconConfig(
            frozenAdmonition,
            frozenCustom,
            ComputeFingerprint(frozenAdmonition, frozenCustom),
            revision);
    }

    /// <summary>
    /// 获取 Admonition 提示块图标（实心字形体系）。
    /// </summary>
    public string GetAdmonitionIcon(string kind) =>
        AdmonitionIcons.TryGetValue(kind, out var icon)
            ? icon
            : AdmonitionIcons[AdmonitionCatalog.DefaultAdmonitionKind];

    /// <summary>
    /// 获取自定义引用块图标（outline 字形体系）。
    /// </summary>
    public string GetCustomBlockIcon(string kind) =>
        CustomBlockIcons.TryGetValue(kind, out var icon)
            ? icon
            : CustomBlockIcons[AdmonitionCatalog.DefaultCustomBlockKind];

    private static string ComputeFingerprint(
        IReadOnlyDictionary<string, string> admonition,
        IReadOnlyDictionary<string, string> custom)
    {
        var builder = new StringBuilder((admonition.Count + custom.Count) * 24);
        AppendTable(builder, 'A', admonition);
        AppendTable(builder, 'C', custom);
        return builder.ToString();
    }

    private static void AppendTable(
        StringBuilder builder,
        char tag,
        IReadOnlyDictionary<string, string> icons)
    {
        var keys = new List<string>(icons.Keys);
        keys.Sort(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            builder.Append(tag).Append(':').Append(key).Append('=').Append(icons[key]).Append(';');
        }
    }
}

/// <summary>图标配置快照：两套字形表与修订号。</summary>
public sealed record AdmonitionIconState(
    IReadOnlyDictionary<string, string> AdmonitionIcons,
    IReadOnlyDictionary<string, string> CustomBlockIcons,
    int Revision);
