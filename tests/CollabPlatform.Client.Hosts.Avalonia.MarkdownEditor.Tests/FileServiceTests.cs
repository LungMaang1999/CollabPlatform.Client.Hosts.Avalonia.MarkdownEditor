using System.IO;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence.Serialization;
using Xunit;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Tests;

/// <summary>
/// 验证 FileService 的样式文件解析与加载分支。
/// </summary>
public class FileServiceTests : IDisposable
{
    private readonly string _tempDir;

    public FileServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "md_editor_fs_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* 忽略清理失败 */ }
    }

    private FileService CreateService()
    {
        var parser = new MarkdownParser(new MarkdownParserOptions());
        var serializer = new DocumentSerializer();
        return new FileService(serializer, parser);
    }

    [Fact]
    public void GetStyleFilePath_ReturnsPathWithStylesSuffix()
    {
        var service = CreateService();
        var mdPath = Path.Combine(_tempDir, "note.md");

        var stylePath = service.GetStyleFilePath(mdPath);

        Assert.Equal(Path.GetFullPath(mdPath) + ".styles.xml", stylePath);
    }

    [Fact]
    public void FindExistingStyleFilePath_NoStyleFile_ReturnsNull()
    {
        var service = CreateService();
        var mdPath = Path.Combine(_tempDir, "note.md");
        File.WriteAllText(mdPath, "hello");

        Assert.Null(service.FindExistingStyleFilePath(mdPath));
    }

    [Fact]
    public void FindExistingStyleFilePath_NewStyleFileExists_ReturnsIt()
    {
        var service = CreateService();
        var mdPath = Path.Combine(_tempDir, "note.md");
        File.WriteAllText(mdPath, "hello");
        var stylePath = mdPath + ".styles.xml";
        File.WriteAllText(stylePath, "<root/>");

        var found = service.FindExistingStyleFilePath(mdPath);

        Assert.Equal(Path.GetFullPath(stylePath), found);
    }

    [Fact]
    public void FindExistingStyleFilePath_LegacyStyleFileExists_ReturnsIt()
    {
        var service = CreateService();
        var mdPath = Path.Combine(_tempDir, "note.md");
        File.WriteAllText(mdPath, "hello");
        var legacyPath = mdPath + ".style.xml";
        File.WriteAllText(legacyPath, "<root/>");

        var found = service.FindExistingStyleFilePath(mdPath);

        Assert.Equal(Path.GetFullPath(legacyPath), found);
    }

    [Fact]
    public async Task LoadAsync_StyleExistsAndLoadStyleTrue_LoadsStyle()
    {
        var service = CreateService();
        var mdPath = Path.Combine(_tempDir, "note.md");
        File.WriteAllText(mdPath, "# Title");

        // 用真实序列化器生成合法样式 XML，避免手写结构不被 SchemaMigrator 接受。
        var doc = new MarkdownParser(new MarkdownParserOptions()).Parse("# Title").Document;
        var styleXml = service.ExportStyleXml(doc);
        var stylePath = mdPath + ".styles.xml";
        File.WriteAllText(stylePath, styleXml);

        var result = await service.LoadAsync(mdPath, loadStyle: true);

        Assert.NotNull(result.Document);
        Assert.NotNull(result.Snapshot);
        Assert.True(result.Snapshot!.StyleFileExists);
        Assert.False(result.Snapshot.StyleLoadDeclined);
        Assert.Equal(Path.GetFullPath(stylePath), result.Snapshot.StyleFilePath);
    }

    [Fact]
    public async Task LoadAsync_StyleExistsAndLoadStyleFalse_DeclinesStyle()
    {
        var service = CreateService();
        var mdPath = Path.Combine(_tempDir, "note.md");
        File.WriteAllText(mdPath, "# Title");
        var stylePath = mdPath + ".styles.xml";
        File.WriteAllText(stylePath, "<root/>");

        var result = await service.LoadAsync(mdPath, loadStyle: false);

        Assert.NotNull(result.Document);
        Assert.NotNull(result.Snapshot);
        Assert.True(result.Snapshot!.StyleFileExists);
        Assert.True(result.Snapshot.StyleLoadDeclined);
    }

    [Fact]
    public async Task LoadAsync_NoStyleFile_ReturnsMissingStyleSnapshot()
    {
        var service = CreateService();
        var mdPath = Path.Combine(_tempDir, "note.md");
        File.WriteAllText(mdPath, "# Title");

        var result = await service.LoadAsync(mdPath, loadStyle: true);

        Assert.NotNull(result.Document);
        Assert.NotNull(result.Snapshot);
        Assert.False(result.Snapshot!.StyleFileExists);
        Assert.False(result.Snapshot.StyleLoadDeclined);
        Assert.Equal(string.Empty, result.Snapshot.StyleFileHash);
    }

    [Fact]
    public async Task LoadAsync_MissingMarkdownFile_ThrowsFileNotFoundException()
    {
        var service = CreateService();
        var mdPath = Path.Combine(_tempDir, "missing.md");

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.LoadAsync(mdPath, loadStyle: true));
    }
}
