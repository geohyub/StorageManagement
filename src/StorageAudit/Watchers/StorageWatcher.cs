namespace StorageAudit.Watchers;

using StorageAudit.Models;
using StorageAudit.Services;

public class StorageWatcher : IDisposable
{
    private readonly string _watchRoot;
    private readonly AuditConfig _config;
    private readonly EventNormalizer _normalizer;
    private readonly SelfEventFilter _selfFilter;
    private readonly ILogger<StorageWatcher> _logger;
    private FileSystemWatcher? _watcher;
    private HashSet<string> _ignorePatterns;

    public bool IsRunning { get; private set; }

    public StorageWatcher(string watchRoot, AuditConfig config,
        EventNormalizer normalizer, SelfEventFilter selfFilter,
        ILogger<StorageWatcher> logger)
    {
        _watchRoot = watchRoot;
        _config = config;
        _normalizer = normalizer;
        _selfFilter = selfFilter;
        _logger = logger;
        _ignorePatterns = new HashSet<string>(config.IgnorePatterns, StringComparer.OrdinalIgnoreCase);
    }

    public void Start()
    {
        if (IsRunning) return;

        try
        {
            _watcher = new FileSystemWatcher(_watchRoot)
            {
                IncludeSubdirectories = _config.IncludeSubdirectories,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime,
                InternalBufferSize = 65536, // 64KB - 이벤트 손실 최소화
                EnableRaisingEvents = false
            };

            _watcher.Created += OnCreated;
            _watcher.Changed += OnChanged;
            _watcher.Deleted += OnDeleted;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;

            _watcher.EnableRaisingEvents = true;
            IsRunning = true;
            _logger.LogInformation("FileSystemWatcher started on: {Root}", _watchRoot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start FileSystemWatcher on: {Root}", _watchRoot);
            throw;
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;

        _watcher?.Dispose();
        _watcher = null;
        IsRunning = false;
        _logger.LogInformation("FileSystemWatcher stopped");
    }

    public void UpdateIgnorePatterns(List<string> patterns)
    {
        _ignorePatterns = new HashSet<string>(patterns, StringComparer.OrdinalIgnoreCase);
    }

    private bool ShouldIgnore(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;

        var relativePath = GetRelativePath(path);
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fileName = Path.GetFileName(path);

        foreach (var pattern in _ignorePatterns)
        {
            // 디렉토리 패턴 매칭
            foreach (var segment in segments)
            {
                if (string.Equals(segment, pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // 와일드카드 패턴 매칭
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                if (MatchesWildcard(fileName, pattern))
                    return true;
            }
        }

        return false;
    }

    private static bool MatchesWildcard(string input, string pattern)
    {
        // 간단한 와일드카드 매칭 (*.tmp, ~$*, etc.)
        if (pattern.StartsWith("*"))
        {
            return input.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        }
        if (pattern.EndsWith("*"))
        {
            return input.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(input, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private string GetRelativePath(string fullPath)
    {
        if (fullPath.StartsWith(_watchRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relative = fullPath[_watchRoot.Length..];
            return relative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        return fullPath;
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        _normalizer.HandleRawEvent(new RawFileEvent
        {
            Timestamp = DateTime.UtcNow,
            ChangeType = WatcherChangeTypes.Created,
            FullPath = e.FullPath,
            Name = e.Name ?? Path.GetFileName(e.FullPath)
        });
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        _normalizer.HandleRawEvent(new RawFileEvent
        {
            Timestamp = DateTime.UtcNow,
            ChangeType = WatcherChangeTypes.Changed,
            FullPath = e.FullPath,
            Name = e.Name ?? Path.GetFileName(e.FullPath)
        });
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        _normalizer.HandleRawEvent(new RawFileEvent
        {
            Timestamp = DateTime.UtcNow,
            ChangeType = WatcherChangeTypes.Deleted,
            FullPath = e.FullPath,
            Name = e.Name ?? Path.GetFileName(e.FullPath)
        });
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldIgnore(e.FullPath) && ShouldIgnore(e.OldFullPath)) return;
        _normalizer.HandleRawEvent(new RawFileEvent
        {
            Timestamp = DateTime.UtcNow,
            ChangeType = WatcherChangeTypes.Renamed,
            FullPath = e.FullPath,
            OldFullPath = e.OldFullPath,
            Name = e.Name ?? Path.GetFileName(e.FullPath),
            OldName = e.OldName ?? Path.GetFileName(e.OldFullPath)
        });
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FileSystemWatcher error");

        // 복구 시도
        try
        {
            Stop();
            Thread.Sleep(1000);
            Start();
            _logger.LogInformation("FileSystemWatcher recovered after error");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover FileSystemWatcher");
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

public class RawFileEvent
{
    public DateTime Timestamp { get; set; }
    public WatcherChangeTypes ChangeType { get; set; }
    public string FullPath { get; set; } = string.Empty;
    public string? OldFullPath { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? OldName { get; set; }
}
