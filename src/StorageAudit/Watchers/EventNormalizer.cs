namespace StorageAudit.Watchers;

using System.Collections.Concurrent;
using StorageAudit.Models;
using StorageAudit.Services;

public class EventNormalizer
{
    private readonly AuditConfig _config;
    private readonly SelfEventFilter _selfFilter;
    private readonly string _watchRoot;
    private readonly string _normalizedWatchRoot; // 캐시: 매번 Path.GetFullPath 방지
    private readonly string _currentUser;          // 캐시: 매번 Environment.UserName 방지
    private readonly ILogger<EventNormalizer> _logger;
    private readonly Action<FileEvent> _onNormalized;
    private readonly ConcurrentDictionary<string, DeduplicationEntry> _recentEvents = new();
    private readonly ConcurrentDictionary<string, PendingCreateDelete> _pendingMoves = new();
    private readonly Timer _flushTimer;

    // 시간순 정리용 큐: FlushPending에서 전체 순회 대신 만료된 것만 빠르게 정리
    private readonly ConcurrentQueue<TimestampedKey> _pendingMoveOrder = new();
    private readonly ConcurrentQueue<TimestampedKey> _recentEventOrder = new();

    public EventNormalizer(AuditConfig config, SelfEventFilter selfFilter,
        string watchRoot, ILogger<EventNormalizer> logger,
        Action<FileEvent> onNormalized)
    {
        _config = config;
        _selfFilter = selfFilter;
        _watchRoot = watchRoot;
        _normalizedWatchRoot = Path.GetFullPath(watchRoot).TrimEnd(Path.DirectorySeparatorChar);
        _currentUser = Environment.UserName;
        _logger = logger;
        _onNormalized = onNormalized;
        _flushTimer = new Timer(FlushPending, null,
            _config.EventDeduplicationWindowMs,
            _config.EventDeduplicationWindowMs);
    }

    /// <summary>
    /// 단일 이벤트 처리 (하위 호환)
    /// </summary>
    public void HandleRawEvent(RawFileEvent raw)
    {
        try
        {
            ProcessSingleEvent(raw);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error normalizing event: {Path}", raw.FullPath);
        }
    }

    /// <summary>
    /// 배치 이벤트 처리: StorageWatcher의 DrainLoop에서 호출.
    /// 한 번의 호출로 수백~천 개 이벤트를 처리하여 메서드 호출 오버헤드 최소화.
    /// </summary>
    public void HandleRawEventBatch(List<RawFileEvent> batch)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            try
            {
                ProcessSingleEvent(batch[i]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error normalizing event: {Path}", batch[i].FullPath);
            }
        }
    }

    private void ProcessSingleEvent(RawFileEvent raw)
    {
        switch (raw.ChangeType)
        {
            case WatcherChangeTypes.Created:
                HandleCreated(raw);
                break;
            case WatcherChangeTypes.Changed:
                HandleChanged(raw);
                break;
            case WatcherChangeTypes.Deleted:
                HandleDeleted(raw);
                break;
            case WatcherChangeTypes.Renamed:
                HandleRenamed(raw);
                break;
        }
    }

    private void HandleCreated(RawFileEvent raw)
    {
        var normalizedPath = NormalizePath(raw.FullPath);
        var key = string.Concat("c:", normalizedPath);

        if (IsDuplicate(key)) return;

        var fileName = Path.GetFileName(raw.FullPath);
        var deleteKey = string.Concat("pd:", fileName);
        if (_pendingMoves.TryRemove(deleteKey, out var pending))
        {
            var direction = DetermineDirection(pending.FullPath, raw.FullPath);
            var actionType = direction == EventDirection.Internal
                ? FileActionType.InternalMove
                : direction == EventDirection.Inbound
                    ? FileActionType.ImportedFromExternal
                    : FileActionType.Moved;

            EmitEvent(new FileEvent
            {
                Timestamp = raw.Timestamp,
                ActionType = actionType,
                FileName = fileName,
                FullPath = raw.FullPath,
                OldPath = pending.FullPath,
                NewPath = raw.FullPath,
                Direction = direction,
                FileSizeBytes = TryGetFileSizeFast(raw.FullPath),
                Extension = Path.GetExtension(raw.FullPath),
                DetectionBasis = "Delete+Create pattern",
                Confidence = EventConfidence.High,
                UserAccount = _currentUser
            });
            return;
        }

        var evt = new FileEvent
        {
            Timestamp = raw.Timestamp,
            ActionType = FileActionType.Created,
            FileName = fileName,
            FullPath = raw.FullPath,
            Direction = IsInsideWatchRootFast(normalizedPath) ? EventDirection.Internal : EventDirection.Inbound,
            FileSizeBytes = TryGetFileSizeFast(raw.FullPath),
            Extension = Path.GetExtension(raw.FullPath),
            DetectionBasis = "FileSystemWatcher.Created",
            Confidence = EventConfidence.Confirmed,
            UserAccount = _currentUser
        };

        if (evt.FileSizeBytes > 0)
        {
            evt.Notes = "New file detected. Could be imported from external source.";
        }

        EmitEvent(evt);
    }

    private void HandleChanged(RawFileEvent raw)
    {
        var key = string.Concat("m:", NormalizePath(raw.FullPath));

        if (IsDuplicate(key)) return;

        EmitEvent(new FileEvent
        {
            Timestamp = raw.Timestamp,
            ActionType = FileActionType.Modified,
            FileName = Path.GetFileName(raw.FullPath),
            FullPath = raw.FullPath,
            Direction = EventDirection.Internal,
            FileSizeBytes = TryGetFileSizeFast(raw.FullPath),
            Extension = Path.GetExtension(raw.FullPath),
            DetectionBasis = "FileSystemWatcher.Changed",
            Confidence = EventConfidence.Confirmed,
            UserAccount = _currentUser
        });
    }

    private void HandleDeleted(RawFileEvent raw)
    {
        var fileName = Path.GetFileName(raw.FullPath);
        var deleteKey = string.Concat("pd:", fileName);

        var entry = new PendingCreateDelete
        {
            FullPath = raw.FullPath,
            Timestamp = raw.Timestamp
        };

        // 동일 파일명의 기존 pending이 있으면 덮어씀 (최신 삭제만 유효)
        _pendingMoves[deleteKey] = entry;
        _pendingMoveOrder.Enqueue(new TimestampedKey { Key = deleteKey, Timestamp = raw.Timestamp });
    }

    private void HandleRenamed(RawFileEvent raw)
    {
        if (raw.OldFullPath == null) return;

        var oldDir = Path.GetDirectoryName(raw.OldFullPath);
        var newDir = Path.GetDirectoryName(raw.FullPath);

        FileActionType actionType;
        EventDirection direction;

        if (string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase))
        {
            actionType = FileActionType.Renamed;
            direction = EventDirection.Internal;
        }
        else
        {
            direction = DetermineDirection(raw.OldFullPath, raw.FullPath);
            actionType = direction == EventDirection.Internal
                ? FileActionType.InternalMove
                : FileActionType.Moved;
        }

        EmitEvent(new FileEvent
        {
            Timestamp = raw.Timestamp,
            ActionType = actionType,
            FileName = Path.GetFileName(raw.FullPath),
            FullPath = raw.FullPath,
            OldPath = raw.OldFullPath,
            NewPath = raw.FullPath,
            Direction = direction,
            FileSizeBytes = TryGetFileSizeFast(raw.FullPath),
            Extension = Path.GetExtension(raw.FullPath),
            DetectionBasis = "FileSystemWatcher.Renamed",
            Confidence = EventConfidence.Confirmed,
            UserAccount = _currentUser
        });
    }

    /// <summary>
    /// 시간순 큐 기반 정리: 전체 딕셔너리 순회 대신
    /// 만료된 엔트리만 큐 앞에서 빠르게 제거 (O(expired) vs O(total))
    /// </summary>
    private void FlushPending(object? state)
    {
        var cutoff = DateTime.UtcNow.AddMilliseconds(-_config.EventDeduplicationWindowMs);

        // 보류된 삭제 이벤트 중 타임아웃된 것 처리
        while (_pendingMoveOrder.TryPeek(out var tk) && tk.Timestamp < cutoff)
        {
            _pendingMoveOrder.TryDequeue(out _);

            if (_pendingMoves.TryRemove(tk.Key, out var pending))
            {
                // pending의 타임스탬프가 cutoff보다 새로우면 다시 넣기
                // (동일 키가 덮어쓰기 되었을 수 있음)
                if (pending.Timestamp >= cutoff)
                {
                    _pendingMoves.TryAdd(tk.Key, pending);
                    continue;
                }

                EmitEvent(new FileEvent
                {
                    Timestamp = pending.Timestamp,
                    ActionType = FileActionType.Deleted,
                    FileName = Path.GetFileName(pending.FullPath),
                    FullPath = pending.FullPath,
                    Direction = EventDirection.Internal,
                    Extension = Path.GetExtension(pending.FullPath),
                    DetectionBasis = "FileSystemWatcher.Deleted (no matching create)",
                    Confidence = EventConfidence.Confirmed,
                    UserAccount = _currentUser,
                    Notes = "File deleted. Could be exported to external destination."
                });
            }
        }

        // 중복 방지 캐시 정리
        while (_recentEventOrder.TryPeek(out var rk) && rk.Timestamp < cutoff)
        {
            _recentEventOrder.TryDequeue(out _);
            _recentEvents.TryRemove(rk.Key, out _);
        }
    }

    public void ForceFlush()
    {
        FlushPending(null);
    }

    private bool IsDuplicate(string key)
    {
        var now = DateTime.UtcNow;
        if (_recentEvents.TryGetValue(key, out var existing))
        {
            if ((now - existing.Timestamp).TotalMilliseconds < _config.EventDeduplicationWindowMs)
            {
                Interlocked.Increment(ref existing.Count);
                return true;
            }
        }
        _recentEvents[key] = new DeduplicationEntry { Timestamp = now, Count = 1 };
        _recentEventOrder.Enqueue(new TimestampedKey { Key = key, Timestamp = now });
        return false;
    }

    private EventDirection DetermineDirection(string fromPath, string toPath)
    {
        var fromInRoot = IsInsideWatchRootFast(NormalizePath(fromPath));
        var toInRoot = IsInsideWatchRootFast(NormalizePath(toPath));

        if (fromInRoot && toInRoot) return EventDirection.Internal;
        if (!fromInRoot && toInRoot) return EventDirection.Inbound;
        if (fromInRoot && !toInRoot) return EventDirection.Outbound;
        return EventDirection.Unknown;
    }

    /// <summary>
    /// 캐시된 normalizedWatchRoot 사용: Path.GetFullPath 호출 1회 절약
    /// </summary>
    private bool IsInsideWatchRootFast(string normalizedPath)
    {
        return normalizedPath.StartsWith(_normalizedWatchRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void EmitEvent(FileEvent evt)
    {
        evt.IsSelfGenerated = _selfFilter.IsSelfGenerated(evt.FullPath)
            || (evt.OldPath != null && _selfFilter.IsSelfGenerated(evt.OldPath));

        _onNormalized(evt);
    }

    /// <summary>
    /// 파일 크기 조회: 대량 복사 시 파일이 아직 쓰는 중일 수 있으므로
    /// 실패하면 즉시 null 반환 (디스크 I/O 최소화)
    /// </summary>
    private static long? TryGetFileSizeFast(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
                return info.Length;
        }
        catch { }
        return null;
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    }

    private class DeduplicationEntry
    {
        public DateTime Timestamp;
        public int Count;
    }

    private class PendingCreateDelete
    {
        public string FullPath { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }

    private struct TimestampedKey
    {
        public string Key;
        public DateTime Timestamp;
    }
}
