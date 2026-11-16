using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;

/// <summary>缓存值：整份伴随文档 + 它对应的远端版本。缓存只做加速，任何时候都能从服务端重建。</summary>
public sealed record CachedCompanionDocument(
    CompanionDocument Document,
    string Version,
    string StylesHash,
    string ElementsHash);

/// <summary>
/// 浏览器/弱网缓存。桌面端可不注册；浏览器端用 IndexedDB 实现。
/// 缓存不承担写回：离线保存直接返回 Offline，不做待同步队列。
/// </summary>
public interface ICompanionDocumentCache
{
    bool TryGet(DocumentKey key, out CachedCompanionDocument value);

    void Set(DocumentKey key, CachedCompanionDocument value);

    void Remove(DocumentKey key);
}

/// <summary>进程内缓存，供桌面端测试与临时加速使用。</summary>
public sealed class MemoryCompanionDocumentCache : ICompanionDocumentCache
{
    private readonly Dictionary<DocumentKey, CachedCompanionDocument> _entries = [];
    private readonly object _gate = new();

    public bool TryGet(DocumentKey key, out CachedCompanionDocument value)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key, out value!);
        }
    }

    public void Set(DocumentKey key, CachedCompanionDocument value)
    {
        lock (_gate)
        {
            _entries[key] = value;
        }
    }

    public void Remove(DocumentKey key)
    {
        lock (_gate)
        {
            _entries.Remove(key);
        }
    }
}
