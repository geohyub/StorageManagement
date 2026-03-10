namespace StorageAudit.Services;

using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using StorageAudit.Models;

public class SqliteLogRepository : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _writeConnection;
    private readonly BlockingCollection<FileEvent> _writeQueue = new(10000);
    private readonly CancellationTokenSource _cts = new();
    private Task? _writerTask;
    private readonly ILogger<SqliteLogRepository> _logger;
    private readonly AuditConfig _config;

    public SqliteLogRepository(string dbPath, ILogger<SqliteLogRepository> logger, AuditConfig config)
    {
        _dbPath = dbPath;
        _logger = logger;
        _config = config;
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

    private void WriteBatch(List<FileEvent> batch)
    {
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

    public PagedResult<FileEvent> Query(EventQuery query)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();

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

        // Sort column whitelist to prevent SQL injection
        var sortCol = query.SortBy switch
        {
            "ActionType" => "action_type",
            "FileName" => "file_name",
            "Alert" => "alert_level",
            "FileSize" => "file_size_bytes",
            _ => "timestamp"
        };
        var sortDir = query.SortDesc ? "DESC" : "ASC";

        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM events {where}";
        countCmd.Parameters.AddRange(parameters.ToArray());
        var totalCount = Convert.ToInt32(countCmd.ExecuteScalar());

        var offset = (query.Page - 1) * query.PageSize;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM events {where} ORDER BY {sortCol} {sortDir} LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddRange(parameters.Select(p => new SqliteParameter(p.ParameterName, p.Value)).ToArray());
        cmd.Parameters.Add(new("$limit", query.PageSize));
        cmd.Parameters.Add(new("$offset", offset));

        var items = new List<FileEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            items.Add(ReadEvent(reader));

        return new PagedResult<FileEvent>
        {
            Items = items,
            TotalCount = totalCount,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    public EventStats GetStats(DateTime? from = null, DateTime? to = null)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();

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

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                COUNT(*) as total,
                SUM(CASE WHEN direction = {(int)EventDirection.Inbound} THEN 1 ELSE 0 END) as imports,
                SUM(CASE WHEN direction = {(int)EventDirection.Outbound} THEN 1 ELSE 0 END) as exports,
                SUM(CASE WHEN action_type = {(int)FileActionType.Deleted} THEN 1 ELSE 0 END) as deletes,
                SUM(CASE WHEN alert_level >= {(int)AlertLevel.Warning} THEN 1 ELSE 0 END) as warnings,
                SUM(CASE WHEN is_self_generated = 1 THEN 1 ELSE 0 END) as self_gen,
                SUM(CASE WHEN action_type = {(int)FileActionType.Created} THEN 1 ELSE 0 END) as creates,
                SUM(CASE WHEN action_type = {(int)FileActionType.Modified} THEN 1 ELSE 0 END) as modifies,
                SUM(CASE WHEN action_type = {(int)FileActionType.Renamed} THEN 1 ELSE 0 END) as renames
            FROM events WHERE {where}";
        cmd.Parameters.AddRange(parameters.ToArray());

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new EventStats
            {
                TotalEvents = reader.GetInt64(0),
                ImportCount = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                ExportCount = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                DeleteCount = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                WarningCount = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                SelfGeneratedCount = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                CreatedCount = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                ModifiedCount = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                RenamedCount = reader.IsDBNull(8) ? 0 : reader.GetInt64(8)
            };
        }
        return new EventStats();
    }

    public List<FileEvent> GetAllEvents(EventQuery query)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();

        var conditions = new List<string>();
        var parameters = new List<SqliteParameter>();
        if (!query.IncludeSelfGenerated)
            conditions.Add("is_self_generated = 0");
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
        if (query.ActionType.HasValue)
        {
            conditions.Add("action_type = $actionType");
            parameters.Add(new("$actionType", (int)query.ActionType.Value));
        }
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM events {where} ORDER BY timestamp DESC";
        cmd.Parameters.AddRange(parameters.ToArray());

        var items = new List<FileEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            items.Add(ReadEvent(reader));
        return items;
    }

    public void ApplyRetentionPolicy()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_config.LogRetentionDays);
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = "DELETE FROM events WHERE timestamp < $cutoff";
            cmd.Parameters.Add(new("$cutoff", cutoff.ToString("o")));
            var deleted = cmd.ExecuteNonQuery();
            if (deleted > 0)
            {
                _logger.LogInformation("Retention policy: deleted {Count} old events", deleted);
                using var optimize = _writeConnection.CreateCommand();
                optimize.CommandText = "PRAGMA optimize";
                optimize.ExecuteNonQuery();
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
