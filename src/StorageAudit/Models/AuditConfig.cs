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

    public int EventBatchIntervalMs { get; set; } = 500;
    public int EventDeduplicationWindowMs { get; set; } = 2000;
    public int MaxEventsPerBatch { get; set; } = 1000;
    public int LogRetentionDays { get; set; } = 90;
    public long MaxDbSizeMb { get; set; } = 500;
    public int WebPort { get; set; } = 19840;
    public bool AutoOpenBrowser { get; set; } = true;
    public bool IncludeSubdirectories { get; set; } = true;

    public int BulkDeleteThreshold { get; set; } = 10;
    public int BulkMoveThreshold { get; set; } = 20;
    public int RapidEventWindowSeconds { get; set; } = 60;
    public int SuspiciousExportThreshold { get; set; } = 5;

    public string SystemFolderName { get; set; } = ".storageaudit";

    public string GetSystemFolder(string root) => Path.Combine(root, SystemFolderName);
    public string GetDbPath(string root) => Path.Combine(GetSystemFolder(root), "logs", "audit.db");
    public string GetExportFolder(string root) => Path.Combine(GetSystemFolder(root), "exports");
    public string GetConfigPath(string root) => Path.Combine(GetSystemFolder(root), "config.json");
}
