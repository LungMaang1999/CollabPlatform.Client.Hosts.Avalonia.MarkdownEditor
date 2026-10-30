using System.IO;
using System.Threading;
using CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence;
using Xunit;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Tests;

/// <summary>
/// 验证 FileWatcherService 的防抖与异常兜底。
/// </summary>
[Collection("FileWatcher")]
public class FileWatcherServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempFile;

    public FileWatcherServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "md_editor_fw_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _tempFile = Path.Combine(_tempDir, "doc.md");
        File.WriteAllText(_tempFile, "initial");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* 忽略清理失败 */ }
    }

    [Fact]
    public async Task Watch_FileChangedAfterDelay_RaisesEvent()
    {
        using var watcher = new FileWatcherService();
        var raised = new TaskCompletionSource<string>();
        watcher.FileChangedOnDisk += (_, path) => raised.TrySetResult(path);
        watcher.Watch(_tempFile);

        // 触发文件变更。
        await Task.Delay(50);
        File.WriteAllText(_tempFile, "changed");

        // 防抖 250ms，等待足够时间。
        var winner = await Task.WhenAny(raised.Task, Task.Delay(2000));
        Assert.Same(raised.Task, winner);
        Assert.Equal(_tempFile, await raised.Task);
    }

    [Fact]
    public async Task Watch_HandlerThrows_DoesNotCrashAndSubsequentEventsStillFire()
    {
        using var watcher = new FileWatcherService();
        var throwCount = 0;
        var secondRaised = new TaskCompletionSource<bool>();

        watcher.FileChangedOnDisk += (_, _) =>
        {
            // 第一次抛异常，验证 catch-all 兜底；第二次正常设置结果。
            if (Interlocked.Increment(ref throwCount) == 1)
            {
                throw new InvalidOperationException("simulated handler failure");
            }
            secondRaised.TrySetResult(true);
        };
        watcher.Watch(_tempFile);

        // 第一次变更：处理器抛异常，应由 catch-all 吞掉。
        await Task.Delay(50);
        File.WriteAllText(_tempFile, "first change");

        // 等待防抖过后再触发第二次变更。
        await Task.Delay(500);
        File.WriteAllText(_tempFile, "second change");

        var winner = await Task.WhenAny(secondRaised.Task, Task.Delay(2000));
        Assert.Same(secondRaised.Task, winner);
        Assert.True(await secondRaised.Task);
        Assert.Equal(2, throwCount);
    }

    [Fact]
    public void Watch_NullOrEmptyPath_Throws()
    {
        using var watcher = new FileWatcherService();
        Assert.ThrowsAny<ArgumentException>(() => watcher.Watch(null!));
        Assert.ThrowsAny<ArgumentException>(() => watcher.Watch("   "));
    }

    [Fact]
    public async Task Stop_PreventsSubsequentEvents()
    {
        using var watcher = new FileWatcherService();
        var raised = false;
        watcher.FileChangedOnDisk += (_, _) => raised = true;
        watcher.Watch(_tempFile);

        watcher.Stop();

        await Task.Delay(50);
        File.WriteAllText(_tempFile, "after stop");

        await Task.Delay(600);
        Assert.False(raised);
    }
}
