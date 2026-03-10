namespace StorageAudit.Services;

using StorageAudit.Models;
using StorageAudit.Watchers;

public class AuditEngine : IHostedService, IDisposable
{
    private readonly ILogger<AuditEngine> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private AuditConfig _config;
    private string _watchRoot = string.Empty;
    private string _machineName = string.Empty;
    private string _storageName = string.Empty;
    private StorageWatcher? _watcher;
    private DriveWatcherService? _driveWatcher;
    private SqliteLogRepository? _repository;
    private ExportService? _exportService;
    private EventNormalizer? _normalizer;
    private SelfEventFilter? _selfFilter;
    private AlertDetector? _alertDetector;
    private Timer? _retentionTimer;

    public SqliteLogRepository? Repository => _repository;
    public ExportService? Export => _exportService;
    public string WatchRoot => _watchRoot;
    public bool IsRunning => _watcher?.IsRunning ?? false;
    public AuditConfig Config => _config;
    public string MachineName => _machineName;
    public string StorageName => _storageName;
    public DriveWatcherService? DriveWatcher => _driveWatcher;

    public AuditEngine(ILogger<AuditEngine> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _config = new AuditConfig();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("AuditEngine starting...");

        var detector = new StorageRootDetector(_loggerFactory.CreateLogger<StorageRootDetector>());
        _config = LoadOrCreateConfig(detector);
        _watchRoot = detector.DetectRoot(_config);
        _config.WatchRoot = _watchRoot;
        detector.EnsureSystemFolders(_watchRoot, _config);

        // PC 이름과 스토리지 이름 설정
        _machineName = _config.MachineName;
        _storageName = !string.IsNullOrEmpty(_config.StorageName)
            ? _config.StorageName
            : detector.DetectStorageName(_watchRoot);
        _config.StorageName = _storageName;

        _selfFilter = new SelfEventFilter();
        _selfFilter.Initialize(_watchRoot, _config);

        _alertDetector = new AlertDetector(_config);

        _repository = new SqliteLogRepository(
            _config.GetDbPath(_watchRoot),
            _loggerFactory.CreateLogger<SqliteLogRepository>(),
            _config,
            _watchRoot);
        _repository.Start();

        _normalizer = new EventNormalizer(
            _config, _selfFilter, _watchRoot,
            _loggerFactory.CreateLogger<EventNormalizer>(),
            OnEventNormalized);

        _exportService = new ExportService(_repository, _config.GetExportFolder(_watchRoot));

        _watcher = new StorageWatcher(
            _watchRoot, _config, _normalizer, _selfFilter,
            _loggerFactory.CreateLogger<StorageWatcher>());
        _watcher.Start();

        // 보조 드라이브(USB 등) 자동 감시 서비스 시작
        _driveWatcher = new DriveWatcherService(
            _config, _normalizer, _selfFilter, _watchRoot, _loggerFactory);
        _driveWatcher.Start();

        _retentionTimer = new Timer(_ => _repository.ApplyRetentionPolicy(),
            null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        _repository.Enqueue(new FileEvent
        {
            Timestamp = DateTime.UtcNow,
            ActionType = FileActionType.Unknown,
            FileName = "StorageAudit",
            FullPath = _watchRoot,
            Direction = EventDirection.Internal,
            DetectionBasis = "System",
            Confidence = EventConfidence.Confirmed,
            Alert = AlertLevel.Info,
            IsSelfGenerated = true,
            MachineName = _machineName,
            StorageName = _storageName,
            Notes = $"Audit engine started. Machine: {_machineName}, Storage: {_storageName}, Watch root: {_watchRoot}"
        });

        SaveConfig();
        _logger.LogInformation("AuditEngine started. Machine: {Machine}, Storage: {Storage}, Watching: {Root}",
            _machineName, _storageName, _watchRoot);
        return Task.CompletedTask;
    }

    private void OnEventNormalized(FileEvent evt)
    {
        // 모든 이벤트에 PC 이름과 스토리지 이름을 스탬프
        evt.MachineName = _machineName;
        evt.StorageName = _storageName;
        evt.Alert = _alertDetector!.Evaluate(evt);
        _repository!.Enqueue(evt);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("AuditEngine stopping...");
        _retentionTimer?.Dispose();
        _driveWatcher?.Stop();
        _watcher?.Stop();
        _normalizer?.ForceFlush();
        _repository?.Dispose();
        return Task.CompletedTask;
    }

    public void UpdateWatchRoot(string newRoot)
    {
        var fullPath = Path.GetFullPath(newRoot);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {fullPath}");

        _watcher?.Stop();
        _driveWatcher?.Stop();
        _watchRoot = fullPath;
        _config.WatchRoot = fullPath;

        var detector = new StorageRootDetector(_loggerFactory.CreateLogger<StorageRootDetector>());
        detector.EnsureSystemFolders(fullPath, _config);
        _selfFilter?.Initialize(fullPath, _config);

        // 스토리지 이름 재감지
        _storageName = detector.DetectStorageName(fullPath);
        _config.StorageName = _storageName;

        _normalizer = new EventNormalizer(
            _config, _selfFilter!, fullPath,
            _loggerFactory.CreateLogger<EventNormalizer>(),
            OnEventNormalized);

        _watcher = new StorageWatcher(
            fullPath, _config, _normalizer, _selfFilter!,
            _loggerFactory.CreateLogger<StorageWatcher>());
        _watcher.Start();

        _driveWatcher = new DriveWatcherService(
            _config, _normalizer, _selfFilter!, fullPath, _loggerFactory);
        _driveWatcher.Start();

        SaveConfig();
    }

    public void UpdateIgnorePatterns(List<string> patterns)
    {
        _config.IgnorePatterns = patterns;
        _watcher?.UpdateIgnorePatterns(patterns);
        SaveConfig();
    }

    private AuditConfig LoadOrCreateConfig(StorageRootDetector detector)
    {
        var tempRoot = detector.DetectRoot(new AuditConfig());
        var configPath = new AuditConfig().GetConfigPath(tempRoot);
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<AuditConfig>(json);
                if (loaded != null)
                {
                    _logger.LogInformation("Config loaded from: {Path}", configPath);
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load config, using defaults");
            }
        }
        return new AuditConfig();
    }

    private void SaveConfig()
    {
        try
        {
            var configPath = _config.GetConfigPath(_watchRoot);
            var json = System.Text.Json.JsonSerializer.Serialize(_config,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save config");
        }
    }

    public void Dispose()
    {
        _retentionTimer?.Dispose();
        _driveWatcher?.Dispose();
        _watcher?.Dispose();
        _repository?.Dispose();
    }
}
