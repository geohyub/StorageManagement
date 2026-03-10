namespace StorageAudit.Watchers;

using System.Collections.Concurrent;
using StorageAudit.Models;
using StorageAudit.Services;

public class EventNormalizer
{
    private readonly AuditConfig _config;
    private readonly SelfEventFilter _selfFilter;
    private readonly string _watchRoot;
    private readonly ILogger<EventNormalizer> _logger;
    private readonly Action<FileEvent> _onNormalized;
    private readonly ConcurrentDictionary<string, DeduplicationEntry> _recentEvents = new();
    private readonly ConcurrentDictionary<string, PendingCreateDelete> _pendingMoves = new();
    private readonly Timer _flushTimer;

    public EventNormalizer(AuditConfig config, SelfEventFilter selfFilter,
        string watchRoot, ILogger<EventNormalizer> logger,
        Action<FileEvent> onNormalized)
    {
        _config = config;
        _selfFilter = selfFilter;
        _watchRoot = watchRoot;
        _logger = logger;
        _onNormalized = onNormalized;
        _flushTimer = new Timer(FlushPending, null,
            _config.EventDeduplicationWindowMs,
            _config.EventDeduplicationWindowMs);
    }

    public void HandleRawEvent(RawFileEvent raw)
    {
        try
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error normalizing event: {Path}", raw.FullPath);
        }
    }

    private void HandleCreated(RawFileEvent raw)
    {
        var key = $"created:{NormalizePath(raw.FullPath)}";

        // 중복 이벤트 억제 (짧은 시간 내 동일 파일에 대한 Created 반복)
        if (IsDuplicate(key)) return;

        // 이동 감지: 삭제 직후 생성 = 이동으로 판별
        var fileName = Path.GetFileName(raw.FullPath);
        var deleteKey = $"pendingdelete:{fileName}";
        if (_pendingMoves.TryRemove(deleteKey, out var pending))
        {
            // 삭제 + 생성 = 이동
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
                FileSizeBytes = TryGetFileSize(raw.FullPath),
                Extension = Path.GetExtension(raw.FullPath),
                DetectionBasis = "Delete+Create pattern (same filename within dedup window)",
                Confidence = EventConfidence.High,
                UserAccount = GetCurrentUser()
            });
            return;
        }

        // 신규 파일 생성
        var evt = new FileEvent
        {
            Timestamp = raw.Timestamp,
            ActionType = FileActionType.Created,
            FileName = fileName,
            FullPath = raw.FullPath,
            Direction = IsInsideWatchRoot(raw.FullPath) ? EventDirection.Internal : EventDirection.Inbound,
            FileSizeBytes = TryGetFileSize(raw.FullPath),
            Extension = Path.GetExtension(raw.FullPath),
            DetectionBasis = "FileSystemWatcher.Created",
            Confidence = EventConfidence.Confirmed,
            UserAccount = GetCurrentUser()
        };

        // 외부에서 반입된 파일인지 추론 (새로 생긴 파일 = 복사/반입 가능성)
        if (evt.FileSizeBytes > 0)
        {
            evt.ActionType = FileActionType.Created;
            evt.Notes = "New file detected. Could be imported from external source.";
            evt.Confidence = EventConfidence.High;
        }

        EmitEvent(evt);
    }

    private void HandleChanged(RawFileEvent raw)
    {
        var key = $"changed:{NormalizePath(raw.FullPath)}";

        // 수정 이벤트 중복 병합 (복사 중 다수의 Changed 이벤트 발생 시 하나로 병합)
        if (IsDuplicate(key)) return;

        EmitEvent(new FileEvent
        {
            Timestamp = raw.Timestamp,
            ActionType = FileActionType.Modified,
            FileName = Path.GetFileName(raw.FullPath),
            FullPath = raw.FullPath,
            Direction = EventDirection.Internal,
            FileSizeBytes = TryGetFileSize(raw.FullPath),
            Extension = Path.GetExtension(raw.FullPath),
            DetectionBasis = "FileSystemWatcher.Changed",
            Confidence = EventConfidence.Confirmed,
            UserAccount = GetCurrentUser()
        });
    }

    private void HandleDeleted(RawFileEvent raw)
    {
        var fileName = Path.GetFileName(raw.FullPath);
        var deleteKey = $"pendingdelete:{fileName}";

        // 삭제를 일시 보류하여 이동/반출 패턴 감지에 활용
        _pendingMoves.TryAdd(deleteKey, new PendingCreateDelete
        {
            FullPath = raw.FullPath,
            Timestamp = raw.Timestamp
        });
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
            // 같은 디렉토리 내 이름 변경
            actionType = FileActionType.Renamed;
            direction = EventDirection.Internal;
        }
        else
        {
            // 다른 디렉토리 = 이동
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
            FileSizeBytes = TryGetFileSize(raw.FullPath),
            Extension = Path.GetExtension(raw.FullPath),
            DetectionBasis = "FileSystemWatcher.Renamed",
            Confidence = EventConfidence.Confirmed,
            UserAccount = GetCurrentUser()
        });
    }

    private void FlushPending(object? state)
    {
        var cutoff = DateTime.UtcNow.AddMilliseconds(-_config.EventDeduplicationWindowMs);

        // 보류된 삭제 이벤트 중 타임아웃된 것 처리
        foreach (var kvp in _pendingMoves)
        {
            if (kvp.Value.Timestamp < cutoff)
            {
                if (_pendingMoves.TryRemove(kvp.Key, out var pending))
                {
                    EmitEvent(new FileEvent
                    {
                        Timestamp = pending.Timestamp,
                        ActionType = FileActionType.Deleted,
                        FileName = Path.GetFileName(pending.FullPath),
                        FullPath = pending.FullPath,
                        Direction = EventDirection.Internal,
                        Extension = Path.GetExtension(pending.FullPath),
                        DetectionBasis = "FileSystemWatcher.Deleted (no matching create within window)",
                        Confidence = EventConfidence.Confirmed,
                        UserAccount = GetCurrentUser(),
                        Notes = "File deleted. Could be exported to external destination."
                    });
                }
            }
        }

        // 중복 방지 캐시 정리
        foreach (var kvp in _recentEvents)
        {
            if (kvp.Value.Timestamp < cutoff)
                _recentEvents.TryRemove(kvp.Key, out _);
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
                existing.Count++;
                return true;
            }
        }
        _recentEvents[key] = new DeduplicationEntry { Timestamp = now, Count = 1 };
        return false;
    }

    private EventDirection DetermineDirection(string fromPath, string toPath)
    {
        var fromInRoot = IsInsideWatchRoot(fromPath);
        var toInRoot = IsInsideWatchRoot(toPath);

        if (fromInRoot && toInRoot) return EventDirection.Internal;
        if (!fromInRoot && toInRoot) return EventDirection.Inbound;
        if (fromInRoot && !toInRoot) return EventDirection.Outbound;
        return EventDirection.Unknown;
    }

    private bool IsInsideWatchRoot(string path)
    {
        var normalized = NormalizePath(path);
        var normalizedRoot = NormalizePath(_watchRoot);
        return normalized.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void EmitEvent(FileEvent evt)
    {
        evt.IsSelfGenerated = _selfFilter.IsSelfGenerated(evt.FullPath)
            || (evt.OldPath != null && _selfFilter.IsSelfGenerated(evt.OldPath));

        _onNormalized(evt);
    }

    private static long? TryGetFileSize(string path)
    {
        try
        {
            if (File.Exists(path))
                return new FileInfo(path).Length;
        }
        catch { /* file may have been deleted */ }
        return null;
    }

    private static string GetCurrentUser()
    {
        return Environment.UserName;
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    }

    private class DeduplicationEntry
    {
        public DateTime Timestamp { get; set; }
        public int Count { get; set; }
    }

    private class PendingCreateDelete
    {
        public string FullPath { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }
}
