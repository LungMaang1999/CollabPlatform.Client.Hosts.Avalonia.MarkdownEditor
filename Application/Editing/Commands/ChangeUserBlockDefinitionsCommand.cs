using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Parsing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Editing.Commands;

/// <summary>
/// 一次元素定义变更：把新定义写盘并定向重解析，撤销时恢复上一版定义
/// 与上一版磁盘内容（包括“原本没有定义文件”）。命令只做“执行/撤销”，
/// 校验与冲突判定在 <see cref="UserBlockDefinitionEditingService"/> 完成。
///
/// 落盘顺序固定为“先写文件、后改内存”：写失败时 <see cref="HasChanges"/>
/// 保持 false，命令栈不会记录，界面也不会看到“加了但没写”。
/// </summary>
internal sealed class ChangeUserBlockDefinitionsCommand : IEditorCommand, IEditorCommandState
{
    private readonly MarkdownDocument _document;
    private readonly IMarkdownParser _parser;
    private readonly string _elementsFilePath;
    private readonly UserBlockElementsFileStamp _stamp;
    private readonly IReadOnlyList<UserBlockDefinition> _before;
    private readonly string? _beforeContent;
    private readonly IReadOnlyList<UserBlockDefinition> _after;
    private readonly string? _afterContent;

    internal ChangeUserBlockDefinitionsCommand(
        MarkdownDocument document,
        IMarkdownParser parser,
        string elementsFilePath,
        UserBlockElementsFileStamp stamp,
        IReadOnlyList<UserBlockDefinition> before,
        string? beforeContent,
        IReadOnlyList<UserBlockDefinition> after,
        string? afterContent,
        string description)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _elementsFilePath = elementsFilePath ?? throw new ArgumentNullException(nameof(elementsFilePath));
        _stamp = stamp ?? throw new ArgumentNullException(nameof(stamp));
        _before = before ?? throw new ArgumentNullException(nameof(before));
        _beforeContent = beforeContent;
        _after = after ?? throw new ArgumentNullException(nameof(after));
        _afterContent = afterContent;
        Description = description;
    }

    public string Description { get; }

    public bool HasChanges { get; private set; }

    public UserBlockDefinitionEditResult Result { get; private set; } = UserBlockDefinitionEditResult.Failure(
        UserBlockDefinitionEditOutcome.SaveFailed,
        "命令尚未执行。");

    public void Execute() => Apply(_after, _afterContent, isUndo: false);

    public void Undo() => Apply(_before, _beforeContent, isUndo: true);

    private void Apply(IReadOnlyList<UserBlockDefinition> target, string? targetContent, bool isUndo)
    {
        var action = isUndo ? "撤销" : "变更";

        try
        {
            var actual = UserBlockElementsFileStore.Read(_elementsFilePath);
            if (!_stamp.Matches(actual))
            {
                throw new UserBlockElementsConflictException(
                    $"元素定义文件在打开后被外部修改（{_elementsFilePath}），本次{action}未写入，"
                    + "内存与磁盘均保持原状。请重新打开文档后再试。");
            }

            // 组合单元随定义文件一起变：从目标文本解析回来，内存与磁盘保持同一份来源。
            var composition = targetContent is null
                ? UserBlockComposition.Empty
                : UserBlockElementsSerializer.Deserialize(targetContent).EffectiveComposition;

            var change = new UserBlockSyntaxChange(
                UserBlockCompositionRules.ExpandAll(target, composition),
                UserBlockSyntaxFingerprint.ComputeChangedIds(_document.UserBlockDefinitions, target));
            var newRoot = _parser.ReparseForUserBlockChange(
                _document.SourceMarkdown,
                _document.Root,
                change);
            var invalidated = InspectInvalidated(_document.Root, newRoot);

            // 先落盘：失败时内存态尚未改动，不会出现“界面显示加了但文件没写”。
            UserBlockElementsFileStore.Write(_elementsFilePath, targetContent);
            _stamp.Update(targetContent);

            _document.ReplaceParsedRoot(newRoot, _document.SourceMarkdown, markChanged: false);
            _document.ApplyUserBlockComposition(composition);
            _document.ApplyUserBlockDefinitions(target);
            // 元素定义默认可能被增删改：同步清理/更新 DefinitionDefault 值，避免残留旧默认。
            _document.ApplyDeclaredAttributeDefaults();
            // 全部内存改写完成后再广播：已打开的编辑器据此按段刷新，不需要重开文档。
            _document.NotifyUserBlockDefinitionsChanged();

            HasChanges = true;
            Result = new UserBlockDefinitionEditResult
            {
                Outcome = UserBlockDefinitionEditOutcome.Succeeded,
                Message = isUndo
                    ? $"已撤销元素定义变更，当前共 {target.Count} 条定义。"
                    : $"{Description}已完成，当前共 {target.Count} 条定义。",
                InvalidatedBlocks = invalidated,
                Definitions = target,
                ElementsFilePath = _elementsFilePath
            };
        }
        catch (UserBlockElementsConflictException ex)
        {
            HasChanges = false;
            Result = UserBlockDefinitionEditResult.Failure(
                UserBlockDefinitionEditOutcome.Conflict,
                ex.Message,
                elementsFilePath: _elementsFilePath,
                definitions: _document.UserBlockDefinitions);

            // 撤销失败必须让调用方感知：命令栈保持原状，不能默默错位。
            if (isUndo) throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HasChanges = false;
            Result = UserBlockDefinitionEditResult.Failure(
                UserBlockDefinitionEditOutcome.SaveFailed,
                $"元素定义写入失败（{_elementsFilePath}）：{ex.Message}。"
                + "定义未生效，磁盘与内存均保持原状。",
                elementsFilePath: _elementsFilePath,
                definitions: _document.UserBlockDefinitions);

            if (isUndo) throw;
        }
    }

    /// <summary>
    /// 通过引用比较找出新树中“没有复用旧节点”的块：这些正是被定向重解析的块，
    /// 其余块由解析器原样复用，不会误报。
    /// </summary>
    private static IReadOnlyList<InvalidatedUserBlock> InspectInvalidated(
        MarkdownNode previousRoot,
        MarkdownNode newRoot)
    {
        var previousBlocks = new HashSet<MarkdownNode>(
            FlattenConcreteBlocks(previousRoot),
            ReferenceEqualityComparer.Instance);

        var blocks = FlattenConcreteBlocks(newRoot);
        var result = new List<InvalidatedUserBlock>();

        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            if (previousBlocks.Contains(block))
            {
                continue;
            }

            result.Add(new InvalidatedUserBlock
            {
                Index = index,
                Type = block.Type,
                CustomKind = block.Attributes.TryGetValue("custom-kind", out var kind) ? kind : null,
                Preview = BuildPreview(block),
                Range = block.Range
            });
        }

        return result;
    }

    private static List<MarkdownNode> FlattenConcreteBlocks(MarkdownNode node)
    {
        var result = new List<MarkdownNode>();
        Collect(node, result);
        return result;

        static void Collect(MarkdownNode current, List<MarkdownNode> sink)
        {
            foreach (var child in current.Children)
            {
                if (child.Type is NodeType.Document or NodeType.Section)
                {
                    Collect(child, sink);
                }
                else
                {
                    sink.Add(child);
                }
            }
        }
    }

    private static string BuildPreview(MarkdownNode block)
    {
        var text = string.IsNullOrWhiteSpace(block.Text) ? block.RawMarkdown : block.Text;
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 60 ? text : text[..60] + "…";
    }
}
