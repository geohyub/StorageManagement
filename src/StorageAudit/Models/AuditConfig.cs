namespace StorageAudit.Models;

public class AuditConfig
{
    public string WatchRoot { get; set; } = string.Empty;
    public List<string> IgnorePatterns { get; set; } = new()
    {
        ".storageaudit",
        ".git",
        "node_modules",
        "$RECYCLE.BIN",
        "System Volume Information",
        "*.tmp",
        "~$*",
        "Thumbs.db",
        "desktop.ini"
    };

    public int EventBatchIntervalMs { get; set; } = 300;
    public int EventDeduplicationWindowMs { get; set; } = 3000;
    public int MaxEventsPerBatch { get; set; } = 5000;
    public int LogRetentionDays { get; set; } = 90;
    public long MaxDbSizeMb { get; set; } = 500;
    public int WebPort { get; set; } = 19840;
    public bool AutoOpenBrowser { get; set; } = true;
    public bool IncludeSubdirectories { get; set; } = true;

    public int BulkDeleteThreshold { get; set; } = 500;
    public int BulkMoveThreshold { get; set; } = 2000;
    public int RapidEventWindowSeconds { get; set; } = 120;
    public int SuspiciousExportThreshold { get; set; } = 500;

    public string SystemFolderName { get; set; } = ".storageaudit";

    public string GetSystemFolder(string root) => Path.Combine(root, SystemFolderName);
    public string GetLogFolder(string root) => Path.Combine(GetSystemFolder(root), "logs");
    public string GetDbPath(string root) => GetDbPathForDate(root, DateTime.Now);
    public string GetDbPathForDate(string root, DateTime date) =>
        Path.Combine(GetLogFolder(root), $"audit_{date:yyyy-MM-dd}.db");
    public string GetExportFolder(string root) => Path.Combine(GetSystemFolder(root), "exports");
    public string GetConfigPath(string root) => Path.Combine(GetSystemFolder(root), "config.json");
}
