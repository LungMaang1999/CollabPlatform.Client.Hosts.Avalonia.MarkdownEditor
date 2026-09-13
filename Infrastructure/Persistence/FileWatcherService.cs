using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Persistence;

public interface IFileWatcherService : IDisposable
{
    void Watch(string filePath);
    void Stop();
    event EventHandler<string>? FileChangedOnDisk;
}

public sealed class FileWatcherService : IFileWatcherService
{
    private FileSystemWatcher? _watcher;
    private string? _currentWatchedPath;
    private readonly object _lock = new();

    private CancellationTokenSource? _debounceCts;
    private readonly TimeSpan _debounceDelay = TimeSpan.FromMilliseconds(250);
    private bool _isDisposed;

    public event EventHandler<string>? FileChangedOnDisk;

    public void Watch(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        lock (_lock)
        {
            if (_isDisposed) return;
            Stop();

            var directory = Path.GetDirectoryName(filePath);
            var fileName = Path.GetFileName(filePath);

            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
                return;

            _currentWatchedPath = filePath;
            _watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (e.ChangeType is not (WatcherChangeTypes.Changed or WatcherChangeTypes.Renamed))
            return;

        var fullPath = e.FullPath;

        lock (_lock)
        {
            if (_isDisposed) return;

            // 取消上一次未执行的通知，防止操作系统短时间内连续派发多次事件
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_debounceDelay, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested)
                    {
                        FileChangedOnDisk?.Invoke(this, fullPath);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常防抖取消
                }
            }, token);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;

            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileSystemEvent;
                _watcher.Renamed -= OnFileSystemEvent;
                _watcher.Dispose();
                _watcher = null;
            }
            _currentWatchedPath = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Stop();
        }
    }
}