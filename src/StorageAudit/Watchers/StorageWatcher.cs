namespace StorageAudit.Watchers;

using System.Collections.Concurrent;
using System.Threading.Channels;
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
    private HashSet<string> _ignorePatterns = new(StringComparer.OrdinalIgnoreCase);

    // FSW 콜백 → Channel → 전용 스레드 → Normalizer (FSW 스레드 즉시 해제)
    private readonly Channel<RawFileEvent> _eventChannel;
    private readonly CancellationTokenSource _drainCts = new();
    private Task? _drainTask;

    // 와일드카드 패턴 캐시 (매번 Contains('*') 체크 방지)
    private List<string> _wildcardPatterns = new();
    private HashSet<string> _exactPatterns = new(StringComparer.OrdinalIgnoreCase);

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

        // Unbounded channel: FSW 콜백을 절대 블로킹하지 않음
        _eventChannel = Channel.CreateUnbounded<RawFileEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        });

        RebuildPatternCache(config.IgnorePatterns);
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
                InternalBufferSize = 262144, // 256KB — 수천 개 파일 대응
                EnableRaisingEvents = false
            };

            _watcher.Created += OnCreated;
            _watcher.Changed += OnChanged;
            _watcher.Deleted += OnDeleted;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;

            // Channel drain 스레드 시작
            _drainTask = Task.Run(() => DrainLoop(_drainCts.Token));

            _watcher.EnableRaisingEvents = true;
            IsRunning = true;
            _logger.LogInformation("FileSystemWatcher started on: {Root} (buffer: 256KB)", _watchRoot);
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

        // Channel 완료 신호 → drain 스레드 종료 대기
        _eventChannel.Writer.TryComplete();
        _drainCts.Cancel();
        _drainTask?.Wait(TimeSpan.FromSeconds(3));

        _logger.LogInformation("FileSystemWatcher stopped");
    }

    /// <summary>
    /// Channel에서 이벤트를 배치로 꺼내서 Normalizer에 전달.
    /// 배치 처리로 lock contention과 GC pressure 최소화.
    /// </summary>
    private async Task DrainLoop(CancellationToken ct)
    {
        var batch = new List<RawFileEvent>(256);
        var reader = _eventChannel.Reader;

        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                batch.Clear();

                // 큐에 쌓인 이벤트를 최대 1024개까지 한 번에 수거
                while (batch.Count < 1024 && reader.TryRead(out var evt))
                    batch.Add(evt);

                if (batch.Count > 0)
                    _normalizer.HandleRawEventBatch(batch);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DrainLoop error");
        }

        // 잔여 이벤트 flush
        batch.Clear();
        while (reader.TryRead(out var remaining))
            batch.Add(remaining);
        if (batch.Count > 0)
            _normalizer.HandleRawEventBatch(batch);
    }

    public void UpdateIgnorePatterns(List<string> patterns)
    {
        RebuildPatternCache(patterns);
    }

    private void RebuildPatternCache(List<string> patterns)
    {
        var wildcards = new List<string>();
        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in patterns)
        {
            if (p.Contains('*') || p.Contains('?'))
                wildcards.Add(p);
            else
                exact.Add(p);
        }
        _wildcardPatterns = wildcards;
        _exactPatterns = exact;
        _ignorePatterns = new HashSet<string>(patterns, StringComparer.OrdinalIgnoreCase);
    }

    private bool ShouldIgnore(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;

        var fileName = Path.GetFileName(path);

        // 1) 정확 매칭 (O(1) HashSet lookup)
        if (_exactPatterns.Contains(fileName)) return true;

        // 2) 경로 세그먼트 검사 (디렉토리 패턴)
        if (_exactPatterns.Count > 0)
        {
            var relativePath = GetRelativePath(path);
            var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var segment in segments)
            {
                if (_exactPatterns.Contains(segment))
                    return true;
            }
        }

        // 3) 와일드카드 패턴 (보통 2-3개이므로 빠름)
        foreach (var pattern in _wildcardPatterns)
        {
            if (MatchesWildcard(fileName, pattern))
                return true;
        }

        return false;
    }

    private static bool MatchesWildcard(string input, string pattern)
    {
        if (pattern.StartsWith("*"))
            return input.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        if (pattern.EndsWith("*"))
            return input.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
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

    // === FSW 콜백: Channel에 enqueue만 하고 즉시 반환 ===

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        _eventChannel.Writer.TryWrite(new RawFileEvent
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
        _eventChannel.Writer.TryWrite(new RawFileEvent
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
        _eventChannel.Writer.TryWrite(new RawFileEvent
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
        _eventChannel.Writer.TryWrite(new RawFileEvent
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
        _drainCts.Dispose();
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
