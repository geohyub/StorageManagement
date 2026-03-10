namespace StorageAudit.Services;

using StorageAudit.Models;
using StorageAudit.Watchers;

public class AuditEngine : IHostedService, IDisposable
{
    private readonly ILogger<AuditEngine> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private AuditConfig _config;
    private string _watchRoot = string.Empty;
    private StorageWatcher? _watcher;
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
            Notes = $"Audit engine started. Watch root: {_watchRoot}"
        });

        SaveConfig();
        _logger.LogInformation("AuditEngine started. Watching: {Root}", _watchRoot);
        return Task.CompletedTask;
    }

    private void OnEventNormalized(FileEvent evt)
    {
        evt.Alert = _alertDetector!.Evaluate(evt);
        _repository!.Enqueue(evt);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("AuditEngine stopping...");
        _retentionTimer?.Dispose();
        _watcher?.Stop();
        _normalizer?.ForceFlush();
        _repository?.Dispose();
        return Task.CompletedTask;
    }

    public void UpdateWatchRoot(string newRoot)
    {
        // 경로 검증: 실제 존재하는 디렉토리여야 함
        var fullPath = Path.GetFullPath(newRoot);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {fullPath}");

        _watcher?.Stop();
        _watchRoot = fullPath;
        _config.WatchRoot = fullPath;

        var detector = new StorageRootDetector(_loggerFactory.CreateLogger<StorageRootDetector>());
        detector.EnsureSystemFolders(fullPath, _config);
        _selfFilter?.Initialize(fullPath, _config);

        _watcher = new StorageWatcher(
            fullPath, _config, _normalizer!, _selfFilter!,
            _loggerFactory.CreateLogger<StorageWatcher>());
        _watcher.Start();
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
        _watcher?.Dispose();
        _repository?.Dispose();
    }
}
