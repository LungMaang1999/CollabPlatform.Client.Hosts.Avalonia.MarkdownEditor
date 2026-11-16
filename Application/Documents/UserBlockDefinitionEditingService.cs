using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Parsing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Documents;

/// <summary>
/// 元素定义（<c>.md.elements.xml</c>）的编辑服务：供界面“新建/修改/删除元素”调用。
/// 每次操作都走同一条固定管线：
/// <list type="number">
/// <item>校验候选定义（结构化问题，不抛异常）；</item>
/// <item>确认磁盘文件没有被外部改过（内容不一致即拒绝覆盖）；</item>
/// <item>先写盘、后改内存，再按变化定义做定向重解析；</item>
/// <item>通过 <see cref="CommandManager"/> 记录为一条可撤销命令。</item>
/// </list>
/// 本服务是有状态的（记住定义文件的上次已知内容），应在一次文档会话内复用同一个实例。
/// </summary>
public sealed class UserBlockDefinitionEditingService
{
    private readonly IDocumentService _documents;
    private readonly IMarkdownParser _parser;
    private readonly CommandManager? _commands;
    private readonly UserBlockElementsFileStamp _stamp = new();
    private readonly object _gate = new();

    public UserBlockDefinitionEditingService(
        IDocumentService documents,
        IMarkdownParser parser,
        CommandManager? commands = null)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _commands = commands;
    }

    /// <summary>定义伴随文件的标准路径（markdown 全路径 + <c>.elements.xml</c>）。</summary>
    public string GetElementsFilePath(string markdownFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownFilePath);
        return Path.GetFullPath(markdownFilePath) + UserBlockElementsSerializer.FileSuffix;
    }

    /// <summary>新增一条元素定义。</summary>
    public UserBlockDefinitionEditResult Create(UserBlockDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Apply("新增元素定义", definition, replacingId: null, delete: false);
    }

    /// <summary>修改一条元素定义；允许同时改 Id（旧 Id 下的块会因此失效）。</summary>
    public UserBlockDefinitionEditResult Update(string existingId, UserBlockDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingId);
        ArgumentNullException.ThrowIfNull(definition);
        return Apply("修改元素定义", definition, existingId, delete: false);
    }

    /// <summary>删除一条元素定义；引用它的块会退回普通 Markdown。</summary>
    public UserBlockDefinitionEditResult Delete(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Apply("删除元素定义", definition: null, id, delete: true);
    }

    private UserBlockDefinitionEditResult Apply(
        string action,
        UserBlockDefinition? definition,
        string? replacingId,
        bool delete)
    {
        lock (_gate)
        {
            var document = _documents.CurrentDocument;
            if (document is null)
            {
                return UserBlockDefinitionEditResult.Failure(
                    UserBlockDefinitionEditOutcome.NoDocument,
                    "当前没有打开的文档，元素定义未做任何修改。");
            }

            if (string.IsNullOrWhiteSpace(document.FilePath))
            {
                return UserBlockDefinitionEditResult.Failure(
                    UserBlockDefinitionEditOutcome.NoDocument,
                    "当前文档没有磁盘路径（来自内存或浏览器），元素定义无法落盘，未做任何修改。");
            }

            var path = GetElementsFilePath(document.FilePath);
            var current = document.UserBlockDefinitions.ToList();

            UserBlockDefinition? previous = null;
            if (replacingId is not null)
            {
                previous = current.FirstOrDefault(
                    item => string.Equals(item.Id, replacingId, StringComparison.OrdinalIgnoreCase));

                if (previous is null)
                {
                    return UserBlockDefinitionEditResult.Failure(
                        UserBlockDefinitionEditOutcome.NotFound,
                        $"元素定义 '{replacingId}' 不存在（可能已被其它操作删除），未做任何修改。",
                        elementsFilePath: path,
                        definitions: current);
                }
            }

            if (!delete)
            {
                var issues = UserBlockDefinitionValidator.Validate(
                    definition!,
                    current,
                    replacingId,
                    document.Composition);
                if (issues.Count > 0)
                {
                    return UserBlockDefinitionEditResult.Failure(
                        UserBlockDefinitionEditOutcome.ValidationFailed,
                        issues[0].Message,
                        issues,
                        path,
                        current,
                        definition);
                }
            }

            var target = BuildTarget(current, definition, previous, delete);

            string? disk;
            try
            {
                disk = UserBlockElementsFileStore.Read(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return UserBlockDefinitionEditResult.Failure(
                    UserBlockDefinitionEditOutcome.SaveFailed,
                    $"元素定义文件无法读取（{path}）：{ex.Message}。"
                    + "文件可能被其它程序占用，未做任何修改。",
                    elementsFilePath: path,
                    definitions: current,
                    definition: definition);
            }

            var conflict = CheckExternalChange(document, path, disk);
            if (conflict is not null)
            {
                return UserBlockDefinitionEditResult.Failure(
                    UserBlockDefinitionEditOutcome.Conflict,
                    conflict,
                    elementsFilePath: path,
                    definitions: current,
                    definition: definition);
            }

            var command = new ChangeUserBlockDefinitionsCommand(
                document,
                _parser,
                path,
                _stamp,
                current,
                disk,
                target,
                UserBlockElementsFileStore.BuildContent(target, document.Composition),
                action);

            if (_commands is null)
            {
                command.Execute();
            }
            else
            {
                _commands.Execute(command);
            }

            var result = command.Result;
            return result with
            {
                Definition = delete ? null : definition,
                UndoRecorded = _commands is not null && command.HasChanges
            };
        }
    }

    /// <summary>
    /// 外部修改判定。首次接触某文档时先做一次“语义对齐”：磁盘定义必须与当前生效定义
    /// 一致，否则视为打开后被改过（或文件里含被加载器跳过的非法条目）；
    /// 之后按上次已知原始文本逐字节比较，任何改动都拒绝覆盖。
    /// 返回 null 表示没有冲突。
    /// </summary>
    private string? CheckExternalChange(MarkdownDocument document, string path, string? diskContent)
    {
        if (!_stamp.BelongsTo(path, document.Id))
        {
            if (!DefinitionsMatch(diskContent, document.UserBlockDefinitions, document.Composition))
            {
                return $"元素定义文件与当前文档生效的定义不一致（{path}）：可能已被外部修改，"
                    + "或文件里含有加载时被跳过的非法条目。为避免覆盖手改内容，本次未写入；"
                    + "请修正文件后重新打开文档。";
            }

            _stamp.Capture(path, document.Id, diskContent);
            return null;
        }

        if (!_stamp.Matches(diskContent))
        {
            return $"元素定义文件在打开后被外部修改（{path}）：文本内容已变化。"
                + "为避免覆盖手改内容，本次未写入；请重新打开文档后再试。";
        }

        return null;
    }

    private static bool DefinitionsMatch(
        string? diskContent,
        IReadOnlyList<UserBlockDefinition> definitions,
        UserBlockComposition composition)
    {
        if (diskContent is null)
        {
            return definitions.Count == 0 && composition.IsEmpty;
        }

        var onDisk = UserBlockElementsSerializer.Deserialize(diskContent);
        return string.Equals(
            UserBlockElementsSerializer.Serialize(onDisk.Definitions, onDisk.EffectiveComposition),
            UserBlockElementsSerializer.Serialize(definitions, composition),
            StringComparison.Ordinal);
    }

    private static List<UserBlockDefinition> BuildTarget(
        List<UserBlockDefinition> current,
        UserBlockDefinition? definition,
        UserBlockDefinition? previous,
        bool delete)
    {
        if (delete)
        {
            return current.Where(item => !ReferenceEquals(item, previous)).ToList();
        }

        if (previous is null)
        {
            return [.. current, definition!];
        }

        var target = new List<UserBlockDefinition>(current.Count);
        foreach (var item in current)
        {
            target.Add(ReferenceEquals(item, previous) ? definition! : item);
        }

        return target;
    }
}
