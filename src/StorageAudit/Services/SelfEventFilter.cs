namespace StorageAudit.Services;

using StorageAudit.Models;

public class SelfEventFilter
{
    private readonly HashSet<string> _selfPatterns = new(StringComparer.OrdinalIgnoreCase);
    private string _systemFolder = string.Empty;

    public void Initialize(string watchRoot, AuditConfig config)
    {
        _systemFolder = Path.GetFullPath(config.GetSystemFolder(watchRoot));
        _selfPatterns.Clear();
        _selfPatterns.Add(config.SystemFolderName);
        _selfPatterns.Add("audit.db");
        _selfPatterns.Add("audit.db-wal");
        _selfPatterns.Add("audit.db-shm");
        _selfPatterns.Add("config.json");
    }

    public bool IsSelfGenerated(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var normalizedPath = Path.GetFullPath(path);
        if (normalizedPath.StartsWith(_systemFolder, StringComparison.OrdinalIgnoreCase))
            return true;
        var fileName = Path.GetFileName(path);
        return _selfPatterns.Contains(fileName);
    }
}
