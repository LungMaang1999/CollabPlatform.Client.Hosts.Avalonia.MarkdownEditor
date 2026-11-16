using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Diagnostics;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Application.Abstractions.Documents;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence;

/// <summary>
/// 本地文件文档的伴随文档存储：整份 <c>&lt;doc&gt;.md.companion.xml</c> 一次原子写入。
///
/// 迁移规则（首次保存时才做，打开从不写盘）：
/// <list type="number">
/// <item>先把每个旧文件复制成 <c>.bak-{yyyyMMddHHmmss}</c>；</item>
/// <item>再原子安装伴随文件；</item>
/// <item>最后删除旧文件。任一步失败都回滚：恢复被删的旧文件、撤掉刚安装的伴随文件、
/// 清掉本次备份，整次保存返回失败且旧文件原样不动。</item>
/// </list>
///
/// 读取规则：伴随文件存在时它是唯一来源，旧文件与 <c>.bak-*</c> 一律不读；
/// 只有伴随文件不存在时才按固定文件名（精确路径，永不通配）读取旧文件。
/// </summary>
public sealed class FileCompanionDocumentStore : ICompanionDocumentStore
{
    internal const string FaultStageAfterBackup = "after-backup";
    internal const string FaultStageAfterInstall = "after-install";
    internal const string FaultStageBeforeDelete = "before-delete";
    internal const string FaultStageAfterDelete = "after-delete";

    private const string CompanionVersionKind = "companion";
    private const string LegacyVersionKind = "legacy";
    private const string EmptyVersion = "empty";
    private const string LegacyStyleFileSuffix = ".styles.xml";
    private const string LegacyStyleFileSuffixOld = ".style.xml";
    private const string BackupInfix = ".bak-";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>测试用故障注入：在每个阶段后触发，用于验证“任一步失败整次保存失败”。</summary>
    internal Action<string>? FaultHook { get; set; }

    /// <summary>测试用时钟：备份文件名的时间戳。</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public static string GetCompanionFilePath(string markdownFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownFilePath);
        return Path.GetFullPath(markdownFilePath) + CompanionDocumentSerializer.FileSuffix;
    }

    public static string GetElementsFilePath(string markdownFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownFilePath);
        return Path.GetFullPath(markdownFilePath) + UserBlockElementsSerializer.FileSuffix;
    }

    /// <summary>旧样式文件的精确候选路径（新命名优先，兼容旧命名）；不存在返回 null。</summary>
    public static string? FindLegacyStylesFilePath(string markdownFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownFilePath);
        var fullPath = Path.GetFullPath(markdownFilePath);
        var preferred = fullPath + LegacyStyleFileSuffix;
        if (File.Exists(preferred)) return preferred;

        var legacy = fullPath + LegacyStyleFileSuffixOld;
        return File.Exists(legacy) ? legacy : null;
    }

    /// <summary>
    /// 探测目标位置是否存在“不可安全覆盖”的输入（XML 损坏、结构不认识、版本高于支持）。
    /// 返回精确路径与原因；<c>null</c> 表示可以写入。调用方必须在写任何文件之前用它做前置检查。
    /// </summary>
    public async Task<(string Path, string Reason)?> FindUnreadableInputAsync(
        DocumentKey key,
        CancellationToken cancellationToken = default)
    {
        var markdownPath = ResolveLocalPath(key);
        var companionPath = GetCompanionFilePath(markdownPath);
        var companionText = File.Exists(companionPath)
            ? await ReadAllTextAsync(companionPath, cancellationToken).ConfigureAwait(false)
            : null;

        return FindUnreadableInput(companionPath, companionText, ExistingLegacyFiles(markdownPath));
    }

    public async Task<CompanionSnapshot> LoadAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        var markdownPath = ResolveLocalPath(key);
        var companionPath = GetCompanionFilePath(markdownPath);

        if (File.Exists(companionPath))
        {
            var text = await ReadAllTextAsync(companionPath, cancellationToken).ConfigureAwait(false);
            var document = CompanionDocumentSerializer.Deserialize(text);
            var stylesHash = CompanionDocumentFingerprint.ComputeStylesHash(document.Styles);
            var elementsHash = CompanionDocumentFingerprint.ComputeElementsHash(document.Definitions, document.Composition);
            var companionVersion = EncodeCompanionVersion(Sha256(text), stylesHash, elementsHash);

            // 旧文件被忽略，但把路径报给界面，便于提示清理。
            return new CompanionSnapshot(
                document,
                CompanionFileExists: true,
                companionVersion,
                stylesHash,
                elementsHash,
                ExistingLegacyFiles(markdownPath),
                document.Diagnostics);
        }

        var legacyStylesPath = FindLegacyStylesFilePath(markdownPath);
        var legacyElementsPath = GetElementsFilePath(markdownPath);
        var hasElements = File.Exists(legacyElementsPath);

        if (legacyStylesPath is null && !hasElements)
        {
            return new CompanionSnapshot(
                new CompanionDocument { Format = CompanionDocumentFormat.None },
                CompanionFileExists: false,
                EmptyVersion,
                string.Empty,
                string.Empty,
                [],
                []);
        }

        var diagnostics = new List<DiagnosticMessage>();
        CompanionDocument? stylesDocument = null;
        CompanionDocument? elementsDocument = null;

        if (legacyStylesPath is not null)
        {
            var stylesXml = await ReadAllTextAsync(legacyStylesPath, cancellationToken).ConfigureAwait(false);
            stylesDocument = CompanionDocumentSerializer.FromLegacyStyles(stylesXml);
            diagnostics.AddRange(stylesDocument.Diagnostics);
        }

        if (hasElements)
        {
            var elementsXml = await ReadAllTextAsync(legacyElementsPath, cancellationToken).ConfigureAwait(false);
            elementsDocument = CompanionDocumentSerializer.FromLegacyElements(elementsXml);
            diagnostics.AddRange(elementsDocument.Diagnostics);
        }

        var merged = new CompanionDocument
        {
            Format = CompanionDocumentFormat.LegacyStyles,
            DocumentId = stylesDocument?.DocumentId,
            Styles = stylesDocument?.Styles,
            Definitions = elementsDocument?.Definitions ?? [],
            Composition = elementsDocument?.Composition ?? UserBlockComposition.Empty,
            Diagnostics = diagnostics
        };

        var mergedStylesHash = CompanionDocumentFingerprint.ComputeStylesHash(merged.Styles);
        var mergedElementsHash = CompanionDocumentFingerprint.ComputeElementsHash(merged.Definitions, merged.Composition);
        var version = EncodeLegacyVersion(
            legacyStylesPath is null ? null : await ComputeHashAsync(legacyStylesPath, cancellationToken).ConfigureAwait(false),
            hasElements ? await ComputeHashAsync(legacyElementsPath, cancellationToken).ConfigureAwait(false) : null,
            mergedStylesHash,
            mergedElementsHash);

        return new CompanionSnapshot(
            merged,
            CompanionFileExists: false,
            version,
            mergedStylesHash,
            mergedElementsHash,
            ExistingLegacyFiles(markdownPath),
            diagnostics);
    }

    public async Task<CompanionWriteResult> SaveAsync(
        DocumentKey key,
        CompanionDocument document,
        string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var markdownPath = ResolveLocalPath(key);
        var companionPath = GetCompanionFilePath(markdownPath);

        if (document.IsReadOnly)
        {
            return CompanionWriteResult.Failed(
                CompanionSaveStatus.Forbidden,
                $"{document.ReadOnlyReason ?? "伴随文档为只读，已拒绝写入。"} 文件：{companionPath}");
        }

        var newText = CompanionDocumentSerializer.Serialize(document);
        var newCore = CompanionDocumentSerializer.Serialize(document, includeMeta: false);
        var newStylesHash = CompanionDocumentFingerprint.ComputeStylesHash(document.Styles);
        var newElementsHash = CompanionDocumentFingerprint.ComputeElementsHash(document.Definitions, document.Composition);

        var legacyPaths = ExistingLegacyFiles(markdownPath);
        var companionExists = File.Exists(companionPath);
        var previousText = companionExists
            ? await ReadAllTextAsync(companionPath, cancellationToken).ConfigureAwait(false)
            : null;

        // 0. 输入损坏即拒绝写入：绝不把“解析失败”当成“没有定义”而覆盖原文件。
        var unreadable = FindUnreadableInput(companionPath, previousText, legacyPaths);
        if (unreadable is not null)
        {
            var message =
                $"伴随文件当前不可安全覆盖，已拒绝写入：{unreadable.Value.Path}。" +
                $"原因：{unreadable.Value.Reason} " +
                "请先备份或修复该文件，或显式选择“另存为新文件”。";
            return new CompanionWriteResult(
                CompanionSaveStatus.Forbidden,
                null,
                null,
                [InputErrorDiagnostic(message)],
                message);
        }

        // 1. 冲突：按段报告，先于任何磁盘写入。
        if (expectedVersion is not null)
        {
            var conflict = await CheckConflictAsync(
                expectedVersion,
                companionPath,
                previousText,
                legacyPaths,
                document.Definitions,
                cancellationToken).ConfigureAwait(false);
            if (conflict is not null)
            {
                return new CompanionWriteResult(
                    CompanionSaveStatus.Conflict,
                    null,
                    conflict,
                    [],
                    DescribeConflict(conflict));
            }
        }

        // 2. 内容没变：不触碰磁盘（meta 的写入时间不参与比较）。
        if (previousText is not null)
        {
            var existing = CompanionDocumentSerializer.Deserialize(previousText);
            var existingCore = CompanionDocumentSerializer.Serialize(existing, includeMeta: false);
            if (string.Equals(existingCore, newCore, StringComparison.Ordinal))
            {
                return CompanionWriteResult.NoChange(EncodeCompanionVersion(
                    Sha256(previousText),
                    CompanionDocumentFingerprint.ComputeStylesHash(existing.Styles),
                    CompanionDocumentFingerprint.ComputeElementsHash(existing.Definitions, existing.Composition)));
            }
        }

        var directory = Path.GetDirectoryName(markdownPath);
        if (string.IsNullOrEmpty(directory))
        {
            return CompanionWriteResult.Failed(CompanionSaveStatus.IoFailure, $"伴随文档路径无效：{markdownPath}");
        }

        Directory.CreateDirectory(directory);

        var timestamp = UtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var createdBackups = new List<LegacyBackup>();
        var deletedBackups = new List<LegacyBackup>();

        try
        {
            // 阶段 1：只复制、不删除——任何后续失败都不会动到旧文件。
            foreach (var legacyPath in legacyPaths)
            {
                var backup = new LegacyBackup(legacyPath, legacyPath + BackupInfix + timestamp);
                File.Copy(legacyPath, backup.BackupPath, overwrite: true);
                createdBackups.Add(backup);
            }

            FaultHook?.Invoke(FaultStageAfterBackup);

            // 阶段 2：原子安装伴随文件。
            WriteAtomically(companionPath, newText);

            FaultHook?.Invoke(FaultStageAfterInstall);

            // 阶段 3：删除旧文件——此时伴随文件已是权威。
            foreach (var backup in createdBackups)
            {
                FaultHook?.Invoke(FaultStageBeforeDelete);
                File.Delete(backup.SourcePath);
                deletedBackups.Add(backup);
            }

            FaultHook?.Invoke(FaultStageAfterDelete);

            if (deletedBackups.Count > 0)
            {
                TrimOlderBackups(deletedBackups);
            }

            var installedText = await ReadAllTextAsync(companionPath, cancellationToken).ConfigureAwait(false);
            return CompanionWriteResult.Saved(
                EncodeCompanionVersion(Sha256(installedText), newStylesHash, newElementsHash),
                BuildMigrationDiagnostics(deletedBackups));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var rollback = Rollback(companionPath, previousText, deletedBackups, createdBackups);
            var message = deletedBackups.Count > 0 || createdBackups.Count > 0
                ? $"伴随文档写入失败（{companionPath}）：{ex.Message}。整次保存已失败，旧文件保持原样。"
                : $"伴随文档写入失败（{companionPath}）：{ex.Message}。原文件保持原样。";

            return new CompanionWriteResult(
                CompanionSaveStatus.IoFailure,
                null,
                null,
                rollback,
                rollback.Count == 0 ? message : message + " 部分回滚未能完成，请检查目录权限。");
        }
    }

    /// <summary>
    /// 找出无法读取的输入（伴随文件优先；没有伴随文件时检查旧文件）。
    /// 只要解析出错就返回，调用方必须拒绝写入——损坏不等于空定义。
    /// </summary>
    private static (string Path, string Reason)? FindUnreadableInput(
        string companionPath,
        string? companionText,
        IReadOnlyList<string> legacyPaths)
    {
        if (companionText is not null)
        {
            var document = CompanionDocumentSerializer.Deserialize(companionText);
            var error = document.Diagnostics
                .FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            return document.IsReadOnly || error is not null
                ? (companionPath, error?.Message ?? document.ReadOnlyReason ?? "伴随文档无法读取。")
                : null;
        }

        foreach (var path in legacyPaths)
        {
            var document = path.EndsWith(UserBlockElementsSerializer.FileSuffix, StringComparison.OrdinalIgnoreCase)
                ? CompanionDocumentSerializer.FromLegacyElements(File.ReadAllText(path))
                : CompanionDocumentSerializer.FromLegacyStyles(File.ReadAllText(path));

            var error = document.Diagnostics
                .FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            if (document.IsReadOnly || error is not null)
            {
                return (path, error?.Message ?? document.ReadOnlyReason ?? "旧伴随文件无法读取。");
            }
        }

        return null;
    }

    private static DiagnosticMessage InputErrorDiagnostic(string message) => new()
    {
        Severity = DiagnosticSeverity.Error,
        Code = "COMPANION_INPUT_CORRUPT",
        Message = message,
        Range = SourceRange.Empty()
    };

    /// <summary>计算旧文件在磁盘上的分段哈希，用于迁移前的冲突判定。</summary>
    private async Task<(string StylesHash, string ElementsHash)> ComputeLegacyHashesAsync(
        string markdownPath,
        CancellationToken cancellationToken)
    {
        var stylesPath = FindLegacyStylesFilePath(markdownPath);
        var elementsPath = GetElementsFilePath(markdownPath);

        var stylesDocument = stylesPath is null
            ? null
            : CompanionDocumentSerializer.FromLegacyStyles(
                await ReadAllTextAsync(stylesPath, cancellationToken).ConfigureAwait(false));

        var elementsDocument = !File.Exists(elementsPath)
            ? null
            : CompanionDocumentSerializer.FromLegacyElements(
                await ReadAllTextAsync(elementsPath, cancellationToken).ConfigureAwait(false));

        return (
            CompanionDocumentFingerprint.ComputeStylesHash(stylesDocument?.Styles),
            CompanionDocumentFingerprint.ComputeElementsHash(elementsDocument?.Definitions ?? []));
    }

    private async Task<CompanionChangeReport?> CheckConflictAsync(
        string expectedVersion,
        string companionPath,
        string? companionText,
        IReadOnlyList<string> legacyPaths,
        IReadOnlyList<UserBlockDefinition> incomingDefinitions,
        CancellationToken cancellationToken)
    {
        if (!TryParseVersion(expectedVersion, out var baseline))
        {
            // 无法识别的版本不阻断保存：它不携带可用于分段的基线。
            return null;
        }

        if (string.Equals(baseline.Kind, CompanionVersionKind, StringComparison.Ordinal))
        {
            if (companionText is null)
            {
                return new CompanionChangeReport(
                    StylesChanged: true,
                    ElementsChanged: true,
                    RemoteVersion: EmptyVersion,
                    ChangedDefinitionIds: null);
            }

            var currentFileHash = Sha256(companionText);
            if (string.Equals(currentFileHash, baseline.FileHash, StringComparison.Ordinal))
            {
                return null;
            }

            var current = CompanionDocumentSerializer.Deserialize(companionText);
            var currentStyles = CompanionDocumentFingerprint.ComputeStylesHash(current.Styles);
            var currentElements = CompanionDocumentFingerprint.ComputeElementsHash(current.Definitions);

            // 只有 meta / EditorState 之类的非内容字段变了：不算冲突。
            if (string.Equals(currentStyles, baseline.Styles, StringComparison.Ordinal)
                && string.Equals(currentElements, baseline.Elements, StringComparison.Ordinal))
            {
                return null;
            }

            // 磁盘上的定义段与本进程即将写入的完全一致：这次“外部”改动就是定义编辑服务
            // 刚才写入的自身变更，写回同样的内容不会丢失任何人的数据，不算冲突。
            var incomingElements = CompanionDocumentFingerprint.ComputeElementsHash(incomingDefinitions);
            var stylesChanged = !string.Equals(currentStyles, baseline.Styles, StringComparison.Ordinal);
            var elementsChanged =
                !string.Equals(currentElements, baseline.Elements, StringComparison.Ordinal)
                && !string.Equals(currentElements, incomingElements, StringComparison.Ordinal);

            if (!stylesChanged && !elementsChanged)
            {
                return null;
            }

            return new CompanionChangeReport(
                StylesChanged: stylesChanged,
                ElementsChanged: elementsChanged,
                RemoteVersion: EncodeCompanionVersion(currentFileHash, currentStyles, currentElements),
                ChangedDefinitionIds: elementsChanged
                    ? UserBlockSyntaxFingerprint.ComputeChangedIds(current.Definitions, incomingDefinitions).ToArray()
                    : null);
        }

        if (string.Equals(baseline.Kind, LegacyVersionKind, StringComparison.Ordinal))
        {
            if (File.Exists(companionPath))
            {
                // 打开后被其它程序迁移成了伴随文件：以磁盘为准，拒绝覆盖。
                var currentText = await ReadAllTextAsync(companionPath, cancellationToken).ConfigureAwait(false);
                var current = CompanionDocumentSerializer.Deserialize(currentText);
                var currentStyles = CompanionDocumentFingerprint.ComputeStylesHash(current.Styles);
                var currentElements = CompanionDocumentFingerprint.ComputeElementsHash(current.Definitions);
                return new CompanionChangeReport(
                    StylesChanged: !string.Equals(currentStyles, baseline.Styles, StringComparison.Ordinal),
                    ElementsChanged: !string.Equals(currentElements, baseline.Elements, StringComparison.Ordinal),
                    RemoteVersion: EncodeCompanionVersion(Sha256(currentText), currentStyles, currentElements),
                    ChangedDefinitionIds: null);
            }

            var (stylesHash, elementsHash) = await ComputeLegacyHashesAsync(
                ResolveMarkdownPathFromCompanion(companionPath),
                cancellationToken).ConfigureAwait(false);

            if (string.Equals(stylesHash, baseline.Styles, StringComparison.Ordinal)
                && string.Equals(elementsHash, baseline.Elements, StringComparison.Ordinal))
            {
                return null;
            }

            return new CompanionChangeReport(
                StylesChanged: !string.Equals(stylesHash, baseline.Styles, StringComparison.Ordinal),
                ElementsChanged: !string.Equals(elementsHash, baseline.Elements, StringComparison.Ordinal),
                RemoteVersion: EncodeLegacyVersion(null, null, stylesHash, elementsHash),
                ChangedDefinitionIds: null);
        }

        // baseline 为空：磁盘上突然出现了内容，视为外部改动。
        if (companionText is not null || legacyPaths.Count > 0)
        {
            return new CompanionChangeReport(
                StylesChanged: true,
                ElementsChanged: true,
                RemoteVersion: EmptyVersion,
                ChangedDefinitionIds: null);
        }

        return null;
    }

    private static string DescribeConflict(CompanionChangeReport conflict) => conflict switch
    {
        { StylesChanged: true, ElementsChanged: true } => "文本文档样式与元素定义都已被其他程序修改，本次未写入。",
        { StylesChanged: true } => "文本文档样式已被其他程序修改，本次未写入。",
        { ElementsChanged: true } => "元素定义已被其他程序修改，本次未写入。",
        _ => "伴随文档已被其他程序修改，本次未写入。"
    };

    private static IReadOnlyList<DiagnosticMessage> BuildMigrationDiagnostics(IReadOnlyList<LegacyBackup> backups)
    {
        if (backups.Count == 0)
        {
            return [];
        }

        var names = string.Join("、", backups.Select(backup => Path.GetFileName(backup.BackupPath)));
        return
        [
            new DiagnosticMessage
            {
                Severity = DiagnosticSeverity.Info,
                Code = "COMPANION_MIGRATED",
                Message = $"已把旧的伴随文件合并为 .md.companion.xml，原文件备份为：{names}。",
                Range = SourceRange.Empty()
            }
        ];
    }

    /// <summary>
    /// 回滚：恢复被删的旧文件、撤掉本次安装的伴随文件、清掉本次备份。
    /// 返回回滚过程中自身出现的问题（正常情况下为空）。
    /// </summary>
    private static IReadOnlyList<DiagnosticMessage> Rollback(
        string companionPath,
        string? previousText,
        IReadOnlyList<LegacyBackup> deleted,
        IReadOnlyList<LegacyBackup> created)
    {
        var errors = new List<DiagnosticMessage>();

        foreach (var backup in deleted)
        {
            try
            {
                if (!File.Exists(backup.SourcePath) && File.Exists(backup.BackupPath))
                {
                    File.Copy(backup.BackupPath, backup.SourcePath, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(RollbackFailure($"无法恢复旧文件 {backup.SourcePath}", ex));
            }
        }

        try
        {
            if (previousText is null)
            {
                if (File.Exists(companionPath)) File.Delete(companionPath);
            }
            else
            {
                WriteAtomically(companionPath, previousText);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(RollbackFailure($"无法还原伴随文件 {companionPath}", ex));
        }

        foreach (var backup in created)
        {
            try
            {
                // 旧文件已恢复（或从未被删）才清理备份，避免把唯一的副本删掉。
                if (File.Exists(backup.SourcePath) && File.Exists(backup.BackupPath))
                {
                    File.Delete(backup.BackupPath);
                }
            }
            catch
            {
                // 备份清理失败不影响主流程。
            }
        }

        return errors;
    }

    private static DiagnosticMessage RollbackFailure(string message, Exception ex) => new()
    {
        Severity = DiagnosticSeverity.Error,
        Code = "COMPANION_ROLLBACK_FAILED",
        Message = $"{message}：{ex.Message}",
        Range = SourceRange.Empty()
    };

    /// <summary>只保留最近一份 <c>.bak-*</c>：迁移成功后的备份清理，绝不把备份当输入扫描。</summary>
    private static void TrimOlderBackups(IReadOnlyList<LegacyBackup> backups)
    {
        foreach (var backup in backups)
        {
            try
            {
                var directory = Path.GetDirectoryName(backup.BackupPath);
                if (string.IsNullOrEmpty(directory)) continue;

                var prefix = Path.GetFileName(backup.SourcePath) + BackupInfix;
                var stale = Directory.EnumerateFiles(directory, prefix + "*", SearchOption.TopDirectoryOnly)
                    .Where(path => !string.Equals(path, backup.BackupPath, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var path in stale)
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 清理失败只影响目录整洁，不影响本次保存结果。
            }
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new IOException($"伴随文档路径无效：{path}");
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(tempPath, Utf8NoBom.GetBytes(content));

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // 清理失败不掩盖主异常。
            }
        }
    }

    /// <summary>精确文件名的候选集合；绝不通配，因此 <c>.bak-*</c> 与临时文件永远不会被当作输入。</summary>
    private static List<string> ExistingLegacyFiles(string markdownPath)
    {
        var result = new List<string>(3);
        var fullPath = Path.GetFullPath(markdownPath);

        var preferredStyle = fullPath + LegacyStyleFileSuffix;
        var oldStyle = fullPath + LegacyStyleFileSuffixOld;
        var elements = fullPath + UserBlockElementsSerializer.FileSuffix;

        if (File.Exists(preferredStyle)) result.Add(preferredStyle);
        if (File.Exists(oldStyle)) result.Add(oldStyle);
        if (File.Exists(elements)) result.Add(elements);

        return result;
    }

    private static string ResolveLocalPath(DocumentKey key)
    {
        if (key.Kind != DocumentLocationKind.LocalFile)
        {
            throw new NotSupportedException(
                $"文件伴随文档存储只处理本地文件，收到 {key.Kind}：{key.Value}。");
        }

        return Path.GetFullPath(key.Value);
    }

    private static string ResolveMarkdownPathFromCompanion(string companionPath)
    {
        // 反向去掉固定后缀，避免把 markdown 路径一路传参。
        return companionPath.EndsWith(CompanionDocumentSerializer.FileSuffix, StringComparison.OrdinalIgnoreCase)
            ? companionPath[..^CompanionDocumentSerializer.FileSuffix.Length]
            : companionPath;
    }

    private static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            path,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string EncodeCompanionVersion(string fileHash, string stylesHash, string elementsHash) =>
        $"{CompanionVersionKind}|f={fileHash}|s={stylesHash}|e={elementsHash}";

    private static string EncodeLegacyVersion(
        string? stylesFileHash,
        string? elementsFileHash,
        string stylesHash,
        string elementsHash) =>
        $"{LegacyVersionKind}|fs={stylesFileHash ?? "-"}|fe={elementsFileHash ?? "-"}|s={stylesHash}|e={elementsHash}";

    private static bool TryParseVersion(string version, out ParsedVersion parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var parts = version.Split('|');
        var kind = parts[0];
        if (!string.Equals(kind, CompanionVersionKind, StringComparison.Ordinal)
            && !string.Equals(kind, LegacyVersionKind, StringComparison.Ordinal)
            && !string.Equals(kind, EmptyVersion, StringComparison.Ordinal))
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < parts.Length; index++)
        {
            var separator = parts[index].IndexOf('=');
            if (separator <= 0) continue;

            var key = parts[index][..separator];
            var value = parts[index][(separator + 1)..];
            if (!string.Equals(value, "-", StringComparison.Ordinal))
            {
                values[key] = value;
            }
        }

        parsed = new ParsedVersion(
            kind,
            values.GetValueOrDefault("f") ?? string.Empty,
            values.GetValueOrDefault("s") ?? string.Empty,
            values.GetValueOrDefault("e") ?? string.Empty);
        return true;
    }

    private readonly record struct ParsedVersion(string Kind, string FileHash, string Styles, string Elements);

    private readonly record struct LegacyBackup(string SourcePath, string BackupPath);
}
