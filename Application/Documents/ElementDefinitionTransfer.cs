using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Documents;

public enum DefinitionImportAction
{
    Added,
    Updated,
    Skipped
}

public sealed record DefinitionImportEntry(string Id, DefinitionImportAction Action);

/// <summary>
/// 导入计划：按 Id 匹配逐条“新增 / 覆盖 / 跳过”，导入前可给出数量摘要。
/// <see cref="HasChanges"/> 为 false 时界面必须显示“没有变化”，不能显示成“已保存”。
/// </summary>
public sealed record DefinitionImportPlan(
    IReadOnlyList<DefinitionImportEntry> Entries,
    IReadOnlyList<UserBlockDefinition> Result)
{
    public int AddedCount => Entries.Count(entry => entry.Action == DefinitionImportAction.Added);

    public int UpdatedCount => Entries.Count(entry => entry.Action == DefinitionImportAction.Updated);

    public int SkippedCount => Entries.Count(entry => entry.Action == DefinitionImportAction.Skipped);

    public bool HasChanges => AddedCount + UpdatedCount > 0;

    public string Summary =>
        HasChanges
            ? $"将新增 {AddedCount} 条、覆盖 {UpdatedCount} 条、跳过 {SkippedCount} 条。"
            : "没有变化。";
}

/// <summary>
/// 定义段的导出/导入。导出只产出文本，落盘/下载由调用方（桌面文件选择器、浏览器下载）负责；
/// 导入只做合并计划，不写存储、不记录来源版本。
/// </summary>
public static class ElementDefinitionTransfer
{
    /// <summary>导出仅定义段（<c>.md.elements.xml</c> 内容）。</summary>
    public static string ExportDefinitionsXml(IReadOnlyList<UserBlockDefinition> definitions) =>
        UserBlockElementsSerializer.Serialize(definitions);

    /// <summary>导出整份伴随文档（含样式段）。</summary>
    public static string ExportCompanionXml(CompanionDocument document) =>
        CompanionDocumentSerializer.Serialize(document);

    /// <summary>解析定义段文本；损坏只产生诊断，不抛异常。</summary>
    public static (IReadOnlyList<UserBlockDefinition> Definitions, IReadOnlyList<DiagnosticMessage> Diagnostics)
        ParseDefinitionsXml(string? xml)
    {
        var parsed = UserBlockElementsSerializer.Deserialize(xml);
        return (parsed.Definitions, parsed.Diagnostics);
    }

    /// <summary>解析整份伴随文档文本；损坏/更高版本会被标记为只读并由调用方拒绝覆盖。</summary>
    public static CompanionDocument ParseCompanionXml(string xml) =>
        CompanionDocumentSerializer.Deserialize(xml);

    /// <summary>
    /// 生成导入计划：同 Id 且内容一致 → 跳过；同 Id 内容不同 → 覆盖；新 Id → 新增。
    /// 结果按 Id 稳定排序，便于界面展示与测试。
    /// </summary>
    public static DefinitionImportPlan PlanImport(
        IReadOnlyList<UserBlockDefinition> current,
        IReadOnlyList<UserBlockDefinition> incoming)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);

        var existing = current.ToDictionary(definition => definition.Id, StringComparer.Ordinal);
        var merged = new List<UserBlockDefinition>(current);
        var entries = new List<DefinitionImportEntry>();

        foreach (var candidate in incoming)
        {
            if (!existing.TryGetValue(candidate.Id, out var old))
            {
                merged.Add(candidate);
                entries.Add(new DefinitionImportEntry(candidate.Id, DefinitionImportAction.Added));
                continue;
            }

            if (string.Equals(Serialize(candidate), Serialize(old), StringComparison.Ordinal))
            {
                entries.Add(new DefinitionImportEntry(candidate.Id, DefinitionImportAction.Skipped));
                continue;
            }

            var index = merged.FindIndex(definition => string.Equals(definition.Id, candidate.Id, StringComparison.Ordinal));
            merged[index] = candidate;
            entries.Add(new DefinitionImportEntry(candidate.Id, DefinitionImportAction.Updated));
        }

        return new DefinitionImportPlan(entries, merged);
    }

    private static string Serialize(UserBlockDefinition definition) =>
        UserBlockElementsSerializer.Serialize([definition]);
}
