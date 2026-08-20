using System;
using System.IO;

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

    public event EventHandler<string>? FileChangedOnDisk;

    public void Watch(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        lock (_lock)
        {
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
        if (e.ChangeType is WatcherChangeTypes.Changed or WatcherChangeTypes.Renamed)
        {
            FileChangedOnDisk?.Invoke(this, e.FullPath);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
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

    public void Dispose() => Stop();
}