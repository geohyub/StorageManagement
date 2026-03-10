using Microsoft.Extensions.Logging;
using StorageAudit.Models;
using StorageAudit.Services;

namespace StorageAudit.Tests;

public class SelfEventFilterTests
{
    [Fact]
    public void IsSelfGenerated_SystemFolder_ReturnsTrue()
    {
        var filter = new SelfEventFilter();
        var config = new AuditConfig();
        var root = Path.GetTempPath();
        filter.Initialize(root, config);

        var path = Path.Combine(root, ".storageaudit", "logs", "audit.db");
        Assert.True(filter.IsSelfGenerated(path));
    }

    [Fact]
    public void IsSelfGenerated_UserFile_ReturnsFalse()
    {
        var filter = new SelfEventFilter();
        var config = new AuditConfig();
        var root = Path.GetTempPath();
        filter.Initialize(root, config);

        var path = Path.Combine(root, "documents", "report.docx");
        Assert.False(filter.IsSelfGenerated(path));
    }

    [Fact]
    public void IsSelfGenerated_DbWalFile_ReturnsTrue()
    {
        var filter = new SelfEventFilter();
        var config = new AuditConfig();
        var root = Path.GetTempPath();
        filter.Initialize(root, config);

        var path = Path.Combine(root, ".storageaudit", "logs", "audit.db-wal");
        Assert.True(filter.IsSelfGenerated(path));
    }
}

public class AuditConfigTests
{
    [Fact]
    public void GetSystemFolder_ReturnsCorrectPath()
    {
        var config = new AuditConfig();
        var root = "/storage";
        var expected = Path.Combine(root, ".storageaudit");
        Assert.Equal(expected, config.GetSystemFolder(root));
    }

    [Fact]
    public void GetDbPath_ReturnsDateBasedPathInsideSystemFolder()
    {
        var config = new AuditConfig();
        var root = "/storage";
        var dbPath = config.GetDbPath(root);
        Assert.Contains(".storageaudit", dbPath);
        Assert.Contains("audit_", dbPath);
        Assert.EndsWith(".db", dbPath);
        // 날짜 형식 확인
        Assert.Matches(@"audit_\d{4}-\d{2}-\d{2}\.db$", dbPath);
    }

    [Fact]
    public void DefaultIgnorePatterns_ContainsExpectedEntries()
    {
        var config = new AuditConfig();
        Assert.Contains(".storageaudit", config.IgnorePatterns);
        Assert.Contains(".git", config.IgnorePatterns);
        Assert.Contains("node_modules", config.IgnorePatterns);
    }

    [Fact]
    public void DefaultConfig_HasReasonableDefaults()
    {
        var config = new AuditConfig();
        Assert.Equal(19840, config.WebPort);
        Assert.Equal(90, config.LogRetentionDays);
        Assert.Equal(500, config.MaxDbSizeMb);
        Assert.Equal(500, config.BulkDeleteThreshold);
        Assert.True(config.IncludeSubdirectories);
    }
}

public class AlertDetectorTests
{
    [Fact]
    public void Evaluate_NormalEvent_ReturnsNormal()
    {
        var config = new AuditConfig();
        var detector = new AlertDetector(config);
        var evt = new FileEvent
        {
            ActionType = FileActionType.Modified,
            Direction = EventDirection.Internal,
            FileName = "test.txt",
            FullPath = "/storage/test.txt"
        };
        var result = detector.Evaluate(evt);
        Assert.Equal(AlertLevel.Normal, result);
    }

    [Fact]
    public void Evaluate_SelfGenerated_ReturnsNormal()
    {
        var config = new AuditConfig();
        var detector = new AlertDetector(config);
        var evt = new FileEvent
        {
            ActionType = FileActionType.Deleted,
            Direction = EventDirection.Internal,
            FileName = "audit.db",
            FullPath = "/storage/.storageaudit/logs/audit.db",
            IsSelfGenerated = true
        };
        var result = detector.Evaluate(evt);
        Assert.Equal(AlertLevel.Normal, result);
    }

    [Fact]
    public void Evaluate_OutboundEvent_ReturnsWarning()
    {
        var config = new AuditConfig();
        var detector = new AlertDetector(config);
        var evt = new FileEvent
        {
            ActionType = FileActionType.Moved,
            Direction = EventDirection.Outbound,
            FileName = "secret.docx",
            FullPath = "/storage/secret.docx"
        };
        var result = detector.Evaluate(evt);
        Assert.True(result >= AlertLevel.Warning);
    }

    [Fact]
    public void Evaluate_BulkDelete_ReturnsCritical()
    {
        var config = new AuditConfig { BulkDeleteThreshold = 3, RapidEventWindowSeconds = 60 };
        var detector = new AlertDetector(config);

        AlertLevel lastResult = AlertLevel.Normal;
        for (int i = 0; i < 5; i++)
        {
            var evt = new FileEvent
            {
                ActionType = FileActionType.Deleted,
                Direction = EventDirection.Internal,
                FileName = $"file{i}.txt",
                FullPath = $"/storage/file{i}.txt"
            };
            lastResult = detector.Evaluate(evt);
        }
        Assert.Equal(AlertLevel.Critical, lastResult);
    }

    [Fact]
    public void Evaluate_InboundEvent_ReturnsInfo()
    {
        var config = new AuditConfig();
        var detector = new AlertDetector(config);
        var evt = new FileEvent
        {
            ActionType = FileActionType.Created,
            Direction = EventDirection.Inbound,
            FileName = "imported.pdf",
            FullPath = "/storage/imported.pdf"
        };
        var result = detector.Evaluate(evt);
        Assert.True(result >= AlertLevel.Info);
    }
}

public class FileEventModelTests
{
    [Fact]
    public void FileEvent_DefaultValues_AreCorrect()
    {
        var evt = new FileEvent();
        Assert.Equal(FileActionType.Unknown, evt.ActionType);
        Assert.Equal(EventDirection.Internal, evt.Direction);
        Assert.Equal(EventConfidence.Medium, evt.Confidence);
        Assert.Equal(AlertLevel.Normal, evt.Alert);
        Assert.False(evt.IsSelfGenerated);
    }

    [Fact]
    public void EventQuery_DefaultValues_AreCorrect()
    {
        var query = new EventQuery();
        Assert.Equal(1, query.Page);
        Assert.Equal(100, query.PageSize);
        Assert.Equal("Timestamp", query.SortBy);
        Assert.True(query.SortDesc);
        Assert.False(query.IncludeSelfGenerated);
    }

    [Fact]
    public void PagedResult_TotalPages_CalculatedCorrectly()
    {
        var result = new PagedResult<FileEvent>
        {
            TotalCount = 250,
            PageSize = 50,
            Page = 1
        };
        Assert.Equal(5, result.TotalPages);
    }

    [Fact]
    public void PagedResult_TotalPages_RoundsUp()
    {
        var result = new PagedResult<FileEvent>
        {
            TotalCount = 251,
            PageSize = 50,
            Page = 1
        };
        Assert.Equal(6, result.TotalPages);
    }
}

public class SqliteLogRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteLogRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"storageaudit_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test_audit.db");
    }

    [Fact]
    public void Constructor_CreatesDatabase()
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var config = new AuditConfig();
        using var repo = new SqliteLogRepository(_dbPath,
            loggerFactory.CreateLogger<SqliteLogRepository>(), config, _tempDir);

        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public void EnqueueAndQuery_ReturnsEvents()
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var config = new AuditConfig { EventBatchIntervalMs = 100 };
        using var repo = new SqliteLogRepository(_dbPath,
            loggerFactory.CreateLogger<SqliteLogRepository>(), config, _tempDir);
        repo.Start();

        repo.Enqueue(new FileEvent
        {
            Timestamp = DateTime.UtcNow,
            ActionType = FileActionType.Created,
            FileName = "test.txt",
            FullPath = "/storage/test.txt",
            Direction = EventDirection.Internal,
            Confidence = EventConfidence.Confirmed
        });

        // 배치 쓰기 대기
        Thread.Sleep(500);

        var result = repo.Query(new EventQuery { IncludeSelfGenerated = true });
        Assert.True(result.TotalCount >= 1);
        Assert.Contains(result.Items, e => e.FileName == "test.txt");
    }

    [Fact]
    public void GetStats_ReturnsCorrectCounts()
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var config = new AuditConfig { EventBatchIntervalMs = 100 };
        using var repo = new SqliteLogRepository(_dbPath,
            loggerFactory.CreateLogger<SqliteLogRepository>(), config, _tempDir);
        repo.Start();

        repo.Enqueue(new FileEvent
        {
            Timestamp = DateTime.UtcNow,
            ActionType = FileActionType.Created,
            FileName = "a.txt", FullPath = "/a.txt",
            Direction = EventDirection.Inbound,
            Confidence = EventConfidence.Confirmed
        });
        repo.Enqueue(new FileEvent
        {
            Timestamp = DateTime.UtcNow,
            ActionType = FileActionType.Deleted,
            FileName = "b.txt", FullPath = "/b.txt",
            Direction = EventDirection.Internal,
            Confidence = EventConfidence.Confirmed
        });

        Thread.Sleep(500);

        var stats = repo.GetStats();
        Assert.Equal(2, stats.TotalEvents);
        Assert.Equal(1, stats.ImportCount);
        Assert.Equal(1, stats.DeleteCount);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* cleanup best effort */ }
    }
}
