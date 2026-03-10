namespace StorageAudit.Services;

using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using StorageAudit.Models;

public class SqliteLogRepository : IDisposable
{
    private readonly string _watchRoot;
    private string _dbPath;
    private SqliteConnection _writeConnection;
    private readonly BlockingCollection<FileEvent> _writeQueue = new(50000);
    private readonly CancellationTokenSource _cts = new();
    private Task? _writerTask;
    private readonly ILogger<SqliteLogRepository> _logger;
    private readonly AuditConfig _config;
    private DateTime _currentDbDate;

    public SqliteLogRepository(string dbPath, ILogger<SqliteLogRepository> logger, AuditConfig config, string watchRoot)
    {
        _dbPath = dbPath;
        _watchRoot = watchRoot;
        _logger = logger;
        _config = config;
        _currentDbDate = DateTime.Now.Date;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _writeConnection = new SqliteConnection($"Data Source={dbPath}");
        _writeConnection.Open();
        InitializeDb();
    }

    private void InitializeDb()
    {
        using var cmd = _writeConnection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-2000;
            PRAGMA temp_store=MEMORY;

            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                action_type INTEGER NOT NULL,
                file_name TEXT NOT NULL,
                full_path TEXT NOT NULL,
                old_path TEXT,
                new_path TEXT,
                direction INTEGER NOT NULL,
                file_size_bytes INTEGER,
                extension TEXT,
                detection_basis TEXT,
                confidence INTEGER NOT NULL,
                alert_level INTEGER NOT NULL DEFAULT 0,
                user_account TEXT,
                process_name TEXT,
                process_id INTEGER,
                is_self_generated INTEGER NOT NULL DEFAULT 0,
                group_id TEXT,
                notes TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_events_timestamp ON events(timestamp);
            CREATE INDEX IF NOT EXISTS idx_events_action ON events(action_type);
            CREATE INDEX IF NOT EXISTS idx_events_alert ON events(alert_level);
            CREATE INDEX IF NOT EXISTS idx_events_self ON events(is_self_generated);
            CREATE INDEX IF NOT EXISTS idx_events_path ON events(full_path);
        ";
        cmd.ExecuteNonQuery();
    }

    public void Start()
    {
        _writerTask = Task.Run(() => WriterLoop(_cts.Token));
    }

    public void Enqueue(FileEvent evt)
    {
        if (!_writeQueue.IsAddingCompleted)
            _writeQueue.TryAdd(evt);
    }

    private void WriterLoop(CancellationToken ct)
    {
        var batch = new List<FileEvent>();
        while (!ct.IsCancellationRequested)
        {
            batch.Clear();
            try
            {
                if (_writeQueue.TryTake(out var first, _config.EventBatchIntervalMs, ct))
                {
                    batch.Add(first);
                    while (batch.Count < _config.MaxEventsPerBatch && _writeQueue.TryTake(out var next, 0))
                        batch.Add(next);
                }
            }
            catch (OperationCanceledException) { break; }

            if (batch.Count > 0) WriteBatch(batch);
        }
        // Drain remaining
        while (_writeQueue.TryTake(out var remaining))
            batch.Add(remaining);
        if (batch.Count > 0) WriteBatch(batch);
    }

    private void RotateDbIfNeeded()
    {
        var today = DateTime.Now.Date;
        if (today == _currentDbDate) return;

        try
        {
            _writeConnection.Close();
            _writeConnection.Dispose();

            _currentDbDate = today;
            _dbPath = _config.GetDbPathForDate(_watchRoot, today);
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

            _writeConnection = new SqliteConnection($"Data Source={_dbPath}");
            _writeConnection.Open();
            InitializeDb();

            _logger.LogInformation("Rotated to new daily log: {Path}", _dbPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rotate log DB");
        }
    }

    private void WriteBatch(List<FileEvent> batch)
    {
        RotateDbIfNeeded();
        try
        {
            using var transaction = _writeConnection.BeginTransaction();
            using var cmd = _writeConnection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO events (timestamp, action_type, file_name, full_path, old_path, new_path,
                    direction, file_size_bytes, extension, detection_basis, confidence, alert_level,
                    user_account, process_name, process_id, is_self_generated, group_id, notes)
                VALUES ($ts, $act, $fn, $fp, $op, $np, $dir, $sz, $ext, $det, $conf, $alert,
                    $user, $proc, $pid, $self, $grp, $notes)";

            var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
            var pAct = cmd.Parameters.Add("$act", SqliteType.Integer);
            var pFn = cmd.Parameters.Add("$fn", SqliteType.Text);
            var pFp = cmd.Parameters.Add("$fp", SqliteType.Text);
            var pOp = cmd.Parameters.Add("$op", SqliteType.Text);
            var pNp = cmd.Parameters.Add("$np", SqliteType.Text);
            var pDir = cmd.Parameters.Add("$dir", SqliteType.Integer);
            var pSz = cmd.Parameters.Add("$sz", SqliteType.Integer);
            var pExt = cmd.Parameters.Add("$ext", SqliteType.Text);
            var pDet = cmd.Parameters.Add("$det", SqliteType.Text);
            var pConf = cmd.Parameters.Add("$conf", SqliteType.Integer);
            var pAlert = cmd.Parameters.Add("$alert", SqliteType.Integer);
            var pUser = cmd.Parameters.Add("$user", SqliteType.Text);
            var pProc = cmd.Parameters.Add("$proc", SqliteType.Text);
            var pPid = cmd.Parameters.Add("$pid", SqliteType.Integer);
            var pSelf = cmd.Parameters.Add("$self", SqliteType.Integer);
            var pGrp = cmd.Parameters.Add("$grp", SqliteType.Text);
            var pNotes = cmd.Parameters.Add("$notes", SqliteType.Text);

            foreach (var evt in batch)
            {
                pTs.Value = evt.Timestamp.ToString("o");
                pAct.Value = (int)evt.ActionType;
                pFn.Value = evt.FileName;
                pFp.Value = evt.FullPath;
                pOp.Value = (object?)evt.OldPath ?? DBNull.Value;
                pNp.Value = (object?)evt.NewPath ?? DBNull.Value;
                pDir.Value = (int)evt.Direction;
                pSz.Value = (object?)evt.FileSizeBytes ?? DBNull.Value;
                pExt.Value = (object?)evt.Extension ?? DBNull.Value;
                pDet.Value = (object?)evt.DetectionBasis ?? DBNull.Value;
                pConf.Value = (int)evt.Confidence;
                pAlert.Value = (int)evt.Alert;
                pUser.Value = (object?)evt.UserAccount ?? DBNull.Value;
                pProc.Value = (object?)evt.ProcessName ?? DBNull.Value;
                pPid.Value = (object?)evt.ProcessId ?? DBNull.Value;
                pSelf.Value = evt.IsSelfGenerated ? 1 : 0;
                pGrp.Value = (object?)evt.GroupId ?? DBNull.Value;
                pNotes.Value = (object?)evt.Notes ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write batch of {Count} events", batch.Count);
        }
    }

    private List<string> GetRelevantDbFiles(DateTime? from, DateTime? to)
    {
        var logFolder = _config.GetLogFolder(_watchRoot);
        if (!Directory.Exists(logFolder)) return new List<string> { _dbPath };

        var dbFiles = Directory.GetFiles(logFolder, "audit_*.db")
            .Where(f => !f.EndsWith("-wal") && !f.EndsWith("-shm"))
            .OrderByDescending(f => f)
            .ToList();

        if (dbFiles.Count == 0) return new List<string> { _dbPath };

        if (from.HasValue || to.HasValue)
        {
            dbFiles = dbFiles.Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (name.Length < 16) return true; // audit_yyyy-MM-dd
                var datePart = name["audit_".Length..];
                if (!DateTime.TryParse(datePart, out var fileDate)) return true;
                if (from.HasValue && fileDate.Date > (to?.Date ?? DateTime.MaxValue)) return false;
                if (to.HasValue && fileDate.Date < (from?.Date ?? DateTime.MinValue)) return false;
                return true;
            }).ToList();
        }

        return dbFiles;
    }

    public PagedResult<FileEvent> Query(EventQuery query)
    {
        var (where, parameters) = BuildWhereClause(query);
        var sortCol = query.SortBy switch
        {
            "ActionType" => "action_type",
            "FileName" => "file_name",
            "Alert" => "alert_level",
            "FileSize" => "file_size_bytes",
            _ => "timestamp"
        };
        var sortDir = query.SortDesc ? "DESC" : "ASC";

        var allItems = new List<FileEvent>();
        int totalCount = 0;

        foreach (var dbFile in GetRelevantDbFiles(query.From, query.To))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly");
                conn.Open();

                using var countCmd = conn.CreateCommand();
                countCmd.CommandText = $"SELECT COUNT(*) FROM events {where}";
                countCmd.Parameters.AddRange(CloneParams(parameters));
                totalCount += Convert.ToInt32(countCmd.ExecuteScalar());

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT * FROM events {where} ORDER BY {sortCol} {sortDir}";
                cmd.Parameters.AddRange(CloneParams(parameters));

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    allItems.Add(ReadEvent(reader));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query DB: {File}", dbFile);
            }
        }

        // 메모리에서 정렬 + 페이징
        allItems = sortDir == "DESC"
            ? allItems.OrderByDescending(e => GetSortValue(e, sortCol)).ToList()
            : allItems.OrderBy(e => GetSortValue(e, sortCol)).ToList();

        var offset = (query.Page - 1) * query.PageSize;
        var paged = allItems.Skip(offset).Take(query.PageSize).ToList();

        return new PagedResult<FileEvent>
        {
            Items = paged,
            TotalCount = totalCount,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    private static object GetSortValue(FileEvent e, string col) => col switch
    {
        "action_type" => (int)e.ActionType,
        "file_name" => e.FileName,
        "alert_level" => (int)e.Alert,
        "file_size_bytes" => e.FileSizeBytes ?? 0L,
        _ => e.Timestamp
    };

    private static (string where, List<SqliteParameter> parameters) BuildWhereClause(EventQuery query)
    {
        var conditions = new List<string>();
        var parameters = new List<SqliteParameter>();

        if (!query.IncludeSelfGenerated)
            conditions.Add("is_self_generated = 0");
        if (query.ActionType.HasValue)
        {
            conditions.Add("action_type = $actionType");
            parameters.Add(new("$actionType", (int)query.ActionType.Value));
        }
        if (query.MinAlertLevel.HasValue)
        {
            conditions.Add("alert_level >= $minAlert");
            parameters.Add(new("$minAlert", (int)query.MinAlertLevel.Value));
        }
        if (query.From.HasValue)
        {
            conditions.Add("timestamp >= $from");
            parameters.Add(new("$from", query.From.Value.ToString("o")));
        }
        if (query.To.HasValue)
        {
            conditions.Add("timestamp <= $to");
            parameters.Add(new("$to", query.To.Value.ToString("o")));
        }
        if (!string.IsNullOrEmpty(query.Search))
        {
            conditions.Add("(file_name LIKE $search OR full_path LIKE $search OR notes LIKE $search)");
            parameters.Add(new("$search", $"%{query.Search}%"));
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        return (where, parameters);
    }

    private static SqliteParameter[] CloneParams(List<SqliteParameter> src) =>
        src.Select(p => new SqliteParameter(p.ParameterName, p.Value)).ToArray();

    public EventStats GetStats(DateTime? from = null, DateTime? to = null)
    {
        var conditions = new List<string> { "1=1" };
        var parameters = new List<SqliteParameter>();
        if (from.HasValue)
        {
            conditions.Add("timestamp >= $from");
            parameters.Add(new("$from", from.Value.ToString("o")));
        }
        if (to.HasValue)
        {
            conditions.Add("timestamp <= $to");
            parameters.Add(new("$to", to.Value.ToString("o")));
        }
        var where = string.Join(" AND ", conditions);

        var total = new EventStats();

        foreach (var dbFile in GetRelevantDbFiles(from, to))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly");
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    SELECT
                        COUNT(*) as total,
                        SUM(CASE WHEN direction = {(int)EventDirection.Inbound} THEN 1 ELSE 0 END),
                        SUM(CASE WHEN direction = {(int)EventDirection.Outbound} THEN 1 ELSE 0 END),
                        SUM(CASE WHEN action_type = {(int)FileActionType.Deleted} THEN 1 ELSE 0 END),
                        SUM(CASE WHEN alert_level >= {(int)AlertLevel.Warning} THEN 1 ELSE 0 END),
                        SUM(CASE WHEN is_self_generated = 1 THEN 1 ELSE 0 END),
                        SUM(CASE WHEN action_type = {(int)FileActionType.Created} THEN 1 ELSE 0 END),
                        SUM(CASE WHEN action_type = {(int)FileActionType.Modified} THEN 1 ELSE 0 END),
                        SUM(CASE WHEN action_type = {(int)FileActionType.Renamed} THEN 1 ELSE 0 END)
                    FROM events WHERE {where}";
                cmd.Parameters.AddRange(CloneParams(parameters));

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    total.TotalEvents += reader.GetInt64(0);
                    total.ImportCount += reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    total.ExportCount += reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                    total.DeleteCount += reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                    total.WarningCount += reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
                    total.SelfGeneratedCount += reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
                    total.CreatedCount += reader.IsDBNull(6) ? 0 : reader.GetInt64(6);
                    total.ModifiedCount += reader.IsDBNull(7) ? 0 : reader.GetInt64(7);
                    total.RenamedCount += reader.IsDBNull(8) ? 0 : reader.GetInt64(8);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get stats from: {File}", dbFile);
            }
        }
        return total;
    }

    public List<FileEvent> GetAllEvents(EventQuery query)
    {
        var (where, parameters) = BuildWhereClause(query);
        var allItems = new List<FileEvent>();

        foreach (var dbFile in GetRelevantDbFiles(query.From, query.To))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly");
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT * FROM events {where} ORDER BY timestamp DESC";
                cmd.Parameters.AddRange(CloneParams(parameters));

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    allItems.Add(ReadEvent(reader));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read events from: {File}", dbFile);
            }
        }

        return allItems.OrderByDescending(e => e.Timestamp).ToList();
    }

    public void ApplyRetentionPolicy()
    {
        try
        {
            var logFolder = _config.GetLogFolder(_watchRoot);
            if (!Directory.Exists(logFolder)) return;

            var cutoffDate = DateTime.Now.AddDays(-_config.LogRetentionDays).Date;
            var dbFiles = Directory.GetFiles(logFolder, "audit_*.db");

            foreach (var file in dbFiles)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var datePart = name["audit_".Length..];
                if (DateTime.TryParse(datePart, out var fileDate) && fileDate.Date < cutoffDate)
                {
                    // DB 파일과 WAL/SHM 파일 삭제
                    foreach (var ext in new[] { "", "-wal", "-shm" })
                    {
                        var target = file + ext;
                        if (File.Exists(target))
                        {
                            File.Delete(target);
                            _logger.LogInformation("Retention policy: deleted old log {File}", target);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply retention policy");
        }
    }

    private static FileEvent ReadEvent(SqliteDataReader reader)
    {
        return new FileEvent
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
            ActionType = (FileActionType)reader.GetInt32(reader.GetOrdinal("action_type")),
            FileName = reader.GetString(reader.GetOrdinal("file_name")),
            FullPath = reader.GetString(reader.GetOrdinal("full_path")),
            OldPath = reader.IsDBNull(reader.GetOrdinal("old_path")) ? null : reader.GetString(reader.GetOrdinal("old_path")),
            NewPath = reader.IsDBNull(reader.GetOrdinal("new_path")) ? null : reader.GetString(reader.GetOrdinal("new_path")),
            Direction = (EventDirection)reader.GetInt32(reader.GetOrdinal("direction")),
            FileSizeBytes = reader.IsDBNull(reader.GetOrdinal("file_size_bytes")) ? null : reader.GetInt64(reader.GetOrdinal("file_size_bytes")),
            Extension = reader.IsDBNull(reader.GetOrdinal("extension")) ? null : reader.GetString(reader.GetOrdinal("extension")),
            DetectionBasis = reader.IsDBNull(reader.GetOrdinal("detection_basis")) ? null : reader.GetString(reader.GetOrdinal("detection_basis")),
            Confidence = (EventConfidence)reader.GetInt32(reader.GetOrdinal("confidence")),
            Alert = (AlertLevel)reader.GetInt32(reader.GetOrdinal("alert_level")),
            UserAccount = reader.IsDBNull(reader.GetOrdinal("user_account")) ? null : reader.GetString(reader.GetOrdinal("user_account")),
            ProcessName = reader.IsDBNull(reader.GetOrdinal("process_name")) ? null : reader.GetString(reader.GetOrdinal("process_name")),
            ProcessId = reader.IsDBNull(reader.GetOrdinal("process_id")) ? null : reader.GetInt32(reader.GetOrdinal("process_id")),
            IsSelfGenerated = reader.GetInt32(reader.GetOrdinal("is_self_generated")) == 1,
            GroupId = reader.IsDBNull(reader.GetOrdinal("group_id")) ? null : reader.GetString(reader.GetOrdinal("group_id")),
            Notes = reader.IsDBNull(reader.GetOrdinal("notes")) ? null : reader.GetString(reader.GetOrdinal("notes"))
        };
    }

    public void Dispose()
    {
        _writeQueue.CompleteAdding();
        _cts.Cancel();
        _writerTask?.Wait(TimeSpan.FromSeconds(5));
        _writeConnection.Dispose();
        _cts.Dispose();
    }
}
