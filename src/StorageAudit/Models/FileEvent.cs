namespace StorageAudit.Models;

public enum FileActionType
{
    Unknown = 0,
    Created = 1,
    Modified = 2,
    Deleted = 3,
    Renamed = 4,
    Moved = 5,
    Copied = 6,
    ImportedFromExternal = 7,
    ExportedToExternal = 8,
    InternalMove = 9
}

public enum EventDirection
{
    Unknown = 0,
    Inbound = 1,   // 외부 -> 저장소
    Outbound = 2,  // 저장소 -> 외부
    Internal = 3   // 저장소 내부
}

public enum EventConfidence
{
    Low = 0,
    Medium = 1,
    High = 2,
    Confirmed = 3
}

public enum AlertLevel
{
    Normal = 0,
    Info = 1,
    Warning = 2,
    Critical = 3
}

public class FileEvent
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public FileActionType ActionType { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string? OldPath { get; set; }
    public string? NewPath { get; set; }
    public EventDirection Direction { get; set; } = EventDirection.Internal;
    public long? FileSizeBytes { get; set; }
    public string? Extension { get; set; }
    public string? DetectionBasis { get; set; }
    public EventConfidence Confidence { get; set; } = EventConfidence.Medium;
    public AlertLevel Alert { get; set; } = AlertLevel.Normal;
    public string? UserAccount { get; set; }
    public string? ProcessName { get; set; }
    public int? ProcessId { get; set; }
    public bool IsSelfGenerated { get; set; }
    public string? GroupId { get; set; }
    public string? Notes { get; set; }
}

public class EventQuery
{
    public string? Search { get; set; }
    public FileActionType? ActionType { get; set; }
    public AlertLevel? MinAlertLevel { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public bool IncludeSelfGenerated { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 100;
    public string SortBy { get; set; } = "Timestamp";
    public bool SortDesc { get; set; } = true;
}

public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
}

public class EventStats
{
    public long TotalEvents { get; set; }
    public long ImportCount { get; set; }
    public long ExportCount { get; set; }
    public long DeleteCount { get; set; }
    public long WarningCount { get; set; }
    public long SelfGeneratedCount { get; set; }
    public long CreatedCount { get; set; }
    public long ModifiedCount { get; set; }
    public long RenamedCount { get; set; }
}
