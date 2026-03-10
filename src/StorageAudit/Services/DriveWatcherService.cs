namespace StorageAudit.Services;

using System.Collections.Concurrent;
using StorageAudit.Models;
using StorageAudit.Watchers;

/// <summary>
/// 주기적으로 시스템 드라이브 목록을 폴링하여 새로 연결된 이동식/네트워크 드라이브를
/// 자동으로 감시 대상에 추가하고, 분리된 드라이브의 감시를 해제합니다.
/// 보조 드라이브의 이벤트는 메인 EventNormalizer로 전달되어 교차 매칭됩니다.
/// </summary>
public class DriveWatcherService : IDisposable
{
    private readonly AuditConfig _config;
    private readonly EventNormalizer _normalizer;
    private readonly SelfEventFilter _selfFilter;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DriveWatcherService> _logger;
    private readonly string _primaryRoot;
    private readonly ConcurrentDictionary<string, SecondaryDriveInfo> _activeDrives = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _pollTimer;

    public IReadOnlyDictionary<string, SecondaryDriveInfo> ActiveDrives => _activeDrives;

    public DriveWatcherService(
        AuditConfig config,
        EventNormalizer normalizer,
        SelfEventFilter selfFilter,
        string primaryRoot,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _normalizer = normalizer;
        _selfFilter = selfFilter;
        _primaryRoot = Path.GetFullPath(primaryRoot).TrimEnd(Path.DirectorySeparatorChar);
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DriveWatcherService>();
    }

    public void Start()
    {
        if (!_config.EnableDriveWatcher)
        {
            _logger.LogInformation("DriveWatcherService disabled by config");
            return;
        }

        _pollTimer = new Timer(
            PollDrives, null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(_config.DriveWatcherPollIntervalMs));

        _logger.LogInformation("DriveWatcherService started (poll interval: {Ms}ms)", _config.DriveWatcherPollIntervalMs);
    }

    public void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;

        foreach (var kvp in _activeDrives)
        {
            kvp.Value.Watcher?.Dispose();
            _logger.LogInformation("Stopped watching secondary drive: {Root}", kvp.Key);
        }
        _activeDrives.Clear();
    }

    private void PollDrives(object? state)
    {
        try
        {
            var currentDrives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;

                var driveRoot = drive.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar);

                // 주 감시 대상과 동일한 드라이브는 건너뜀
                if (IsSameDrive(driveRoot, _primaryRoot)) continue;

                // 시스템 드라이브 건너뜀
                if (IsSystemDrive(driveRoot)) continue;

                // 이동식, 네트워크, 비시스템 고정 드라이브만 대상
                if (drive.DriveType != DriveType.Removable &&
                    drive.DriveType != DriveType.Network &&
                    drive.DriveType != DriveType.Fixed)
                    continue;

                currentDrives.Add(driveRoot);

                if (!_activeDrives.ContainsKey(driveRoot))
                {
                    AddDriveWatcher(drive, driveRoot);
                }
            }

            // 분리된 드라이브 감시 해제
            foreach (var kvp in _activeDrives)
            {
                if (!currentDrives.Contains(kvp.Key))
                {
                    if (_activeDrives.TryRemove(kvp.Key, out var removed))
                    {
                        removed.Watcher?.Dispose();
                        _logger.LogInformation("Drive disconnected, stopped watching: {Root} ({Label})",
                            kvp.Key, removed.Label);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling drives");
        }
    }

    private void AddDriveWatcher(DriveInfo drive, string driveRoot)
    {
        try
        {
            var label = !string.IsNullOrWhiteSpace(drive.VolumeLabel)
                ? drive.VolumeLabel
                : drive.DriveType.ToString();

            var watcher = new StorageWatcher(
                drive.RootDirectory.FullName,
                _config,
                _normalizer,
                _selfFilter,
                _loggerFactory.CreateLogger<StorageWatcher>());

            watcher.Start();

            var info = new SecondaryDriveInfo
            {
                DriveRoot = driveRoot,
                Label = label,
                DriveType = drive.DriveType,
                Watcher = watcher,
                ConnectedAt = DateTime.UtcNow
            };

            _activeDrives[driveRoot] = info;
            _logger.LogInformation("New drive detected, started watching: {Root} ({Label}, {Type})",
                driveRoot, label, drive.DriveType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start watcher for drive: {Root}", driveRoot);
        }
    }

    private bool IsSameDrive(string driveRoot, string primaryRoot)
    {
        var primaryDrive = Path.GetPathRoot(primaryRoot)?.TrimEnd(Path.DirectorySeparatorChar) ?? primaryRoot;
        return string.Equals(driveRoot, primaryDrive, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemDrive(string driveRoot)
    {
        try
        {
            var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(systemRoot))
            {
                var systemDrive = Path.GetPathRoot(systemRoot)?.TrimEnd(Path.DirectorySeparatorChar);
                return string.Equals(driveRoot, systemDrive, StringComparison.OrdinalIgnoreCase);
            }
            if (driveRoot == "/" || driveRoot == "") return true;
        }
        catch { }
        return false;
    }

    public void Dispose()
    {
        Stop();
    }
}

public class SecondaryDriveInfo
{
    public string DriveRoot { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public DriveType DriveType { get; set; }
    public StorageWatcher? Watcher { get; set; }
    public DateTime ConnectedAt { get; set; }
}
