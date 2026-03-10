namespace StorageAudit.Watchers;

using System.Collections.Concurrent;
using StorageAudit.Models;
using StorageAudit.Services;

public class EventNormalizer
{
    private readonly AuditConfig _config;
    private readonly SelfEventFilter _selfFilter;
    private readonly string _watchRoot;
    private readonly string _normalizedWatchRoot;
    private readonly string _currentUser;
    private readonly ILogger<EventNormalizer> _logger;
    private readonly Action<FileEvent> _onNormalized;
    private readonly ConcurrentDictionary<string, DeduplicationEntry> _recentEvents = new();
    private readonly ConcurrentDictionary<string, PendingCreateDelete> _pendingMoves = new();
    private readonly Timer _flushTimer;

    // 시간순 정리용 큐
    private readonly ConcurrentQueue<TimestampedKey> _pendingMoveOrder = new();
    private readonly ConcurrentQueue<TimestampedKey> _recentEventOrder = new();

    // 복사(Copy) 감지용 양방향 파일 활동 추적
    // 주 저장소(J:)에서 최근 활동한 파일명 → 타임스탬프+크기
    private readonly ConcurrentDictionary<string, FileActivity> _primaryFileActivity
        = new(StringComparer.OrdinalIgnoreCase);
    // 외부 드라이브(USB 등)에서 최근 활동한 파일명 → 타임스탬프+크기
    private readonly ConcurrentDictionary<string, FileActivity> _externalFileActivity
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<TimestampedKey> _activityCleanupOrder = new();

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

    public void HandleRawEvent(RawFileEvent raw)
    {
        try { ProcessSingleEvent(raw); }
        catch (Exception ex) { _logger.LogError(ex, "Error normalizing event: {Path}", raw.FullPath); }
    }

    public void HandleRawEventBatch(List<RawFileEvent> batch)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            try { ProcessSingleEvent(batch[i]); }
            catch (Exception ex) { _logger.LogError(ex, "Error normalizing event: {Path}", batch[i].FullPath); }
        }
    }

    private void ProcessSingleEvent(RawFileEvent raw)
    {
        switch (raw.ChangeType)
        {
            case WatcherChangeTypes.Created: HandleCreated(raw); break;
            case WatcherChangeTypes.Changed: HandleChanged(raw); break;
            case WatcherChangeTypes.Deleted: HandleDeleted(raw); break;
            case WatcherChangeTypes.Renamed: HandleRenamed(raw); break;
        }
    }

    private void HandleCreated(RawFileEvent raw)
    {
        var normalizedPath = NormalizePath(raw.FullPath);
        var key = string.Concat("c:", normalizedPath);
        if (IsDuplicate(key)) return;

        var fileName = Path.GetFileName(raw.FullPath);
        var fileSize = TryGetFileSizeFast(raw.FullPath);
        var isInPrimary = IsInsideWatchRootFast(normalizedPath);

        // ── 1) 이동(Move) 감지: Delete→Create 교차 매칭 ──
        var deleteKey = string.Concat("pd:", fileName);
        if (_pendingMoves.TryRemove(deleteKey, out var pending))
        {
            var direction = DetermineDirection(pending.FullPath, raw.FullPath);
            var actionType = direction switch
            {
                EventDirection.Internal => FileActionType.InternalMove,
                EventDirection.Inbound  => FileActionType.ImportedFromExternal,
                EventDirection.Outbound => FileActionType.ExportedToExternal,
                _                       => FileActionType.Moved
            };

            EmitEvent(new FileEvent
            {
                Timestamp = raw.Timestamp,
                ActionType = actionType,
                FileName = fileName,
                FullPath = raw.FullPath,
                OldPath = pending.FullPath,
                NewPath = raw.FullPath,
                Direction = direction,
                FileSizeBytes = fileSize,
                Extension = Path.GetExtension(raw.FullPath),
                DetectionBasis = "Delete+Create cross-match (move detected)",
                Confidence = EventConfidence.Confirmed,
                UserAccount = _currentUser
            });
            return;
        }

        // ── 2) 복사(Copy) 감지: 양방향 파일 활동 추적 ──
        if (isInPrimary)
        {
            // 주 저장소에 파일 생성됨 → 최근 외부 드라이브에 같은 파일이 있었는지 확인
            TrackActivity(_primaryFileActivity, fileName, raw.Timestamp, fileSize, raw.FullPath);

            if (_externalFileActivity.TryGetValue(fileName, out var extActivity)
                && IsSizeMatch(fileSize, extActivity.FileSize))
            {
                // 외부에 같은 파일이 있고 + 주 저장소에 새로 생성됨 = Import 복사
                EmitEvent(new FileEvent
                {
                    Timestamp = raw.Timestamp,
                    ActionType = FileActionType.ImportedFromExternal,
                    FileName = fileName,
                    FullPath = raw.FullPath,
                    OldPath = extActivity.FullPath,
                    Direction = EventDirection.Inbound,
                    FileSizeBytes = fileSize,
                    Extension = Path.GetExtension(raw.FullPath),
                    DetectionBasis = "Copy detection: same file exists on external drive",
                    Confidence = EventConfidence.Medium,
                    UserAccount = _currentUser,
                    Notes = $"File copied from external drive ({extActivity.FullPath})"
                });
                return;
            }

            // 일반 파일 생성 (외부 매칭 없음)
            EmitEvent(new FileEvent
            {
                Timestamp = raw.Timestamp,
                ActionType = FileActionType.Created,
                FileName = fileName,
                FullPath = raw.FullPath,
                Direction = EventDirection.Internal,
                FileSizeBytes = fileSize,
                Extension = Path.GetExtension(raw.FullPath),
                DetectionBasis = "FileSystemWatcher.Created",
                Confidence = EventConfidence.Confirmed,
                UserAccount = _currentUser,
                Notes = fileSize > 0 ? "New file detected" : null
            });
        }
        else
        {
            // 외부 드라이브에 파일 생성됨 → 주 저장소에 같은 파일이 있었는지 확인
            TrackActivity(_externalFileActivity, fileName, raw.Timestamp, fileSize, raw.FullPath);

            if (_primaryFileActivity.TryGetValue(fileName, out var primaryActivity)
                && IsSizeMatch(fileSize, primaryActivity.FileSize))
            {
                // 주 저장소에 같은 파일이 있고 + 외부에 새로 생성됨 = Export 복사
                EmitEvent(new FileEvent
                {
                    Timestamp = raw.Timestamp,
                    ActionType = FileActionType.ExportedToExternal,
                    FileName = fileName,
                    FullPath = raw.FullPath,
                    OldPath = primaryActivity.FullPath,
                    Direction = EventDirection.Outbound,
                    FileSizeBytes = fileSize,
                    Extension = Path.GetExtension(raw.FullPath),
                    DetectionBasis = "Copy detection: same file exists in primary storage",
                    Confidence = EventConfidence.Medium,
                    UserAccount = _currentUser,
                    Notes = $"File copied to external drive from primary ({primaryActivity.FullPath})"
                });
                return;
            }

            // 외부 드라이브에서 생성되었으나 주 저장소와 무관한 파일
            EmitEvent(new FileEvent
            {
                Timestamp = raw.Timestamp,
                ActionType = FileActionType.Created,
                FileName = fileName,
                FullPath = raw.FullPath,
                Direction = EventDirection.Outbound,
                FileSizeBytes = fileSize,
                Extension = Path.GetExtension(raw.FullPath),
                DetectionBasis = "FileSystemWatcher.Created (external drive)",
                Confidence = EventConfidence.Low,
                UserAccount = _currentUser,
                Notes = "File created on external drive"
            });
        }
    }

    private void HandleChanged(RawFileEvent raw)
    {
        var normalizedPath = NormalizePath(raw.FullPath);
        var key = string.Concat("m:", normalizedPath);
        if (IsDuplicate(key)) return;

        var isInPrimary = IsInsideWatchRootFast(normalizedPath);
        var fileName = Path.GetFileName(raw.FullPath);
        var fileSize = TryGetFileSizeFast(raw.FullPath);

        // 파일 활동 기록 (Export/Import 복사 감지에 활용)
        if (isInPrimary)
            TrackActivity(_primaryFileActivity, fileName, raw.Timestamp, fileSize, raw.FullPath);
        else
            TrackActivity(_externalFileActivity, fileName, raw.Timestamp, fileSize, raw.FullPath);

        EmitEvent(new FileEvent
        {
            Timestamp = raw.Timestamp,
            ActionType = FileActionType.Modified,
            FileName = fileName,
            FullPath = raw.FullPath,
            Direction = isInPrimary ? EventDirection.Internal : EventDirection.Outbound,
            FileSizeBytes = fileSize,
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

        _pendingMoves[deleteKey] = new PendingCreateDelete
        {
            FullPath = raw.FullPath,
            Timestamp = raw.Timestamp,
            IsFromPrimary = IsInsideWatchRootFast(NormalizePath(raw.FullPath))
        };
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
            actionType = direction switch
            {
                EventDirection.Internal => FileActionType.InternalMove,
                EventDirection.Inbound  => FileActionType.ImportedFromExternal,
                EventDirection.Outbound => FileActionType.ExportedToExternal,
                _                       => FileActionType.Moved
            };
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

    private void FlushPending(object? state)
    {
        var cutoff = DateTime.UtcNow.AddMilliseconds(-_config.EventDeduplicationWindowMs);

        // 보류된 삭제 이벤트 중 타임아웃된 것 처리
        while (_pendingMoveOrder.TryPeek(out var tk) && tk.Timestamp < cutoff)
        {
            _pendingMoveOrder.TryDequeue(out _);

            if (_pendingMoves.TryRemove(tk.Key, out var pending))
            {
                if (pending.Timestamp >= cutoff)
                {
                    _pendingMoves.TryAdd(tk.Key, pending);
                    continue;
                }

                // 주 저장소에서 삭제된 파일은 반출 가능성, 외부에서 삭제된 건 무시
                if (pending.IsFromPrimary)
                {
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
        }

        // 중복 방지 캐시 정리
        while (_recentEventOrder.TryPeek(out var rk) && rk.Timestamp < cutoff)
        {
            _recentEventOrder.TryDequeue(out _);
            _recentEvents.TryRemove(rk.Key, out _);
        }

        // 파일 활동 캐시 정리 (RapidEventWindowSeconds 기반)
        var activityCutoff = DateTime.UtcNow.AddSeconds(-_config.RapidEventWindowSeconds);
        while (_activityCleanupOrder.TryPeek(out var ak) && ak.Timestamp < activityCutoff)
        {
            _activityCleanupOrder.TryDequeue(out _);
            if (ak.Key.StartsWith("p:"))
                _primaryFileActivity.TryRemove(ak.Key[2..], out _);
            else if (ak.Key.StartsWith("e:"))
                _externalFileActivity.TryRemove(ak.Key[2..], out _);
        }
    }

    public void ForceFlush()
    {
        FlushPending(null);
    }

    // === 유틸리티 ===

    private void TrackActivity(ConcurrentDictionary<string, FileActivity> tracker,
        string fileName, DateTime timestamp, long? fileSize, string? fullPath = null)
    {
        var activity = new FileActivity { Timestamp = timestamp, FileSize = fileSize, FullPath = fullPath };
        tracker[fileName] = activity;

        var prefix = ReferenceEquals(tracker, _primaryFileActivity) ? "p:" : "e:";
        _activityCleanupOrder.Enqueue(new TimestampedKey
        {
            Key = string.Concat(prefix, fileName),
            Timestamp = timestamp
        });
    }

    private static bool IsSizeMatch(long? size1, long? size2)
    {
        // 둘 다 null이면 매칭 (크기 알 수 없음 → 파일명으로만 판단)
        if (!size1.HasValue || !size2.HasValue) return true;
        // 정확히 같은 크기
        return size1.Value == size2.Value;
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

    private static long? TryGetFileSizeFast(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) return info.Length;
        }
        catch { }
        return null;
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    }

    // === 내부 클래스 ===

    private class DeduplicationEntry
    {
        public DateTime Timestamp;
        public int Count;
    }

    private class PendingCreateDelete
    {
        public string FullPath { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public bool IsFromPrimary { get; set; }
    }

    private class FileActivity
    {
        public DateTime Timestamp { get; set; }
        public long? FileSize { get; set; }
        public string? FullPath { get; set; }
    }

    private struct TimestampedKey
    {
        public string Key;
        public DateTime Timestamp;
    }
}
