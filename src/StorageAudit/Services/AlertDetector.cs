namespace StorageAudit.Services;

using System.Collections.Concurrent;
using StorageAudit.Models;

public class AlertDetector
{
    private readonly AuditConfig _config;
    private readonly ConcurrentQueue<TimestampedAction> _recentActions = new();

    public AlertDetector(AuditConfig config)
    {
        _config = config;
    }

    public AlertLevel Evaluate(FileEvent evt)
    {
        var now = DateTime.UtcNow;

        // 시간 윈도우 기반 추적
        _recentActions.Enqueue(new TimestampedAction(now, evt.ActionType, evt.Direction));
        CleanOldActions(now);

        // 자기 생성 이벤트는 경고 불필요
        if (evt.IsSelfGenerated) return AlertLevel.Normal;

        var level = AlertLevel.Normal;

        // 외부 반출 의심
        if (evt.Direction == EventDirection.Outbound)
        {
            level = AlertLevel.Warning;
            evt.Notes = (evt.Notes ?? "") + " [ALERT: Possible data export to external location]";
        }

        // 대량 삭제 감지
        var recentDeletes = CountRecentActions(FileActionType.Deleted);
        if (recentDeletes >= _config.BulkDeleteThreshold)
        {
            level = AlertLevel.Critical;
            evt.Notes = (evt.Notes ?? "") + $" [ALERT: Bulk deletion detected ({recentDeletes} files in {_config.RapidEventWindowSeconds}s)]";
        }

        // 대량 이동 감지
        var recentMoves = CountRecentActions(FileActionType.Moved)
            + CountRecentActions(FileActionType.InternalMove);
        if (recentMoves >= _config.BulkMoveThreshold)
        {
            level = MaxLevel(level, AlertLevel.Warning);
            evt.Notes = (evt.Notes ?? "") + $" [ALERT: Bulk move detected ({recentMoves} files in {_config.RapidEventWindowSeconds}s)]";
        }

        // 대량 반출 감지
        var recentExports = CountRecentDirectionActions(EventDirection.Outbound);
        if (recentExports >= _config.SuspiciousExportThreshold)
        {
            level = AlertLevel.Critical;
            evt.Notes = (evt.Notes ?? "") + $" [ALERT: Suspicious mass export ({recentExports} files in {_config.RapidEventWindowSeconds}s)]";
        }

        // 외부 반입
        if (evt.Direction == EventDirection.Inbound)
        {
            level = MaxLevel(level, AlertLevel.Info);
        }

        return level;
    }

    private int CountRecentActions(FileActionType type)
    {
        return _recentActions.Count(a => a.ActionType == type);
    }

    private int CountRecentDirectionActions(EventDirection direction)
    {
        return _recentActions.Count(a => a.Direction == direction);
    }

    private void CleanOldActions(DateTime now)
    {
        var cutoff = now.AddSeconds(-_config.RapidEventWindowSeconds);
        while (_recentActions.TryPeek(out var oldest) && oldest.Timestamp < cutoff)
        {
            _recentActions.TryDequeue(out _);
        }
    }

    private static AlertLevel MaxLevel(AlertLevel a, AlertLevel b) =>
        (AlertLevel)Math.Max((int)a, (int)b);

    private record TimestampedAction(DateTime Timestamp, FileActionType ActionType, EventDirection Direction);
}
