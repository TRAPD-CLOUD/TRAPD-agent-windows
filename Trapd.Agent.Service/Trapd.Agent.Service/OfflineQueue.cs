using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Trapd.Agent.Service;

public sealed class OfflineQueue : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connString;
    private readonly object _lock = new();
    private bool _disposed;

    public OfflineQueue(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Ensure file is not read-only if it exists
        if (File.Exists(_dbPath))
        {
            try
            {
                var attributes = File.GetAttributes(_dbPath);
                if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    File.SetAttributes(_dbPath, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch { /* ignore permission errors here, let sqlite fail if needed */ }
        }

        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Cache = SqliteCacheMode.Shared,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();

        Init();
    }

    private void Init()
    {
        using var con = new SqliteConnection(_connString);
        con.Open();

        // Optional: Check schema version, but migration works without it
        int currentVersion = 0;
        using (var versionCmd = con.CreateCommand())
        {
            versionCmd.CommandText = "PRAGMA user_version;";
            currentVersion = Convert.ToInt32(versionCmd.ExecuteScalar());
        }

        using var sqlCmd = con.CreateCommand();
        sqlCmd.CommandText = @"
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 5000;

CREATE TABLE IF NOT EXISTS queue (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    created_utc TEXT NOT NULL,
    type TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    status INTEGER NOT NULL DEFAULT 0,
    lease_until_utc TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_queue_status_created
ON queue(status, created_utc);

CREATE INDEX IF NOT EXISTS idx_queue_status_id
ON queue(status, id);
";
        sqlCmd.ExecuteNonQuery();

        // Migrate schema if needed
        if (currentVersion < 1)
        {
            TryAddColumn(con, "queue", "retry_count", "INTEGER NOT NULL DEFAULT 0");
            TryAddColumn(con, "queue", "last_error", "TEXT NULL");

            // Update schema version
            using var versionCmd = con.CreateCommand();
            versionCmd.CommandText = "PRAGMA user_version = 1;";
            versionCmd.ExecuteNonQuery();
        }
    }

    public long Enqueue(string type, object payload)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(payload);
        ThrowIfDisposed();

        var json = JsonSerializer.Serialize(payload);

        lock (_lock)
        {
            using var con = new SqliteConnection(_connString);
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
INSERT INTO queue(created_utc, type, payload_json, status, retry_count)
VALUES ($created, $type, $payload, 0, 0);
SELECT last_insert_rowid();
";
            cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$payload", json);

            return (long)cmd.ExecuteScalar()!;
        }
    }

    public long EnqueueRaw(string type, string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(payloadJson);
        ThrowIfDisposed();

        lock (_lock)
        {
            using var con = new SqliteConnection(_connString);
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
INSERT INTO queue(created_utc, type, payload_json, status, retry_count)
VALUES ($created, $type, $payload, 0, 0);
SELECT last_insert_rowid();
";
            cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$payload", payloadJson);

            return (long)cmd.ExecuteScalar()!;
        }
    }

    public List<QueuedItem> LeaseBatch(int batchSize, TimeSpan leaseFor)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        ThrowIfDisposed();

        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(leaseFor).ToString("O");

        lock (_lock)
        {
            using var con = new SqliteConnection(_connString);
            con.Open();

            using var tx = con.BeginTransaction();

            try
            {
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
UPDATE queue
SET status = 0, lease_until_utc = NULL, retry_count = retry_count + 1
WHERE status = 1 AND lease_until_utc IS NOT NULL AND lease_until_utc <= $now;
";
                    cmd.Parameters.AddWithValue("$now", now.ToString("O"));
                    cmd.ExecuteNonQuery();
                }

                var ids = new List<long>();
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
SELECT id FROM queue WHERE status = 0 ORDER BY id ASC LIMIT $limit;
";
                    cmd.Parameters.AddWithValue("$limit", batchSize);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                        ids.Add(reader.GetInt64(0));
                }

                if (ids.Count == 0)
                {
                    tx.Commit();
                    return new List<QueuedItem>();
                }

                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = $"UPDATE queue SET status = 1, lease_until_utc = $leaseUntil WHERE id IN ({string.Join(",", ids)});";
                    cmd.Parameters.AddWithValue("$leaseUntil", leaseUntil);
                    cmd.ExecuteNonQuery();
                }

                var items = new List<QueuedItem>(ids.Count);
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = $"SELECT id, created_utc, type, payload_json, retry_count FROM queue WHERE id IN ({string.Join(",", ids)}) ORDER BY id ASC;";

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        items.Add(new QueuedItem(
                            Id: reader.GetInt64(0),
                            CreatedUtc: reader.GetString(1),
                            Type: reader.GetString(2),
                            PayloadJson: reader.GetString(3),
                            RetryCount: reader.GetInt32(4)
                        ));
                    }
                }

                tx.Commit();
                return items;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    public void MarkSent(IEnumerable<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ThrowIfDisposed();

        var list = ids.ToList();
        if (list.Count == 0) return;

        lock (_lock)
        {
            using var con = new SqliteConnection(_connString);
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = $"UPDATE queue SET status = 2, lease_until_utc = NULL WHERE id IN ({string.Join(",", list)});";
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkDead(IEnumerable<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ThrowIfDisposed();

        var list = ids.ToList();
        if (list.Count == 0) return;

        lock (_lock)
        {
            using var con = new SqliteConnection(_connString);
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = $"UPDATE queue SET status = 3, lease_until_utc = NULL WHERE id IN ({string.Join(",", list)});";
            cmd.ExecuteNonQuery();
        }
    }

    public void ReleaseLease(IEnumerable<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ThrowIfDisposed();

        var list = ids.ToList();
        if (list.Count == 0) return;

        lock (_lock)
        {
            using var con = new SqliteConnection(_connString);
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = $"UPDATE queue SET status = 0, lease_until_utc = NULL, retry_count = retry_count + 1 WHERE id IN ({string.Join(",", list)}) AND status = 1;";
            cmd.ExecuteNonQuery();
        }
    }

    public int DeleteSentItems()
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            using var con = new SqliteConnection(_connString);
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = "DELETE FROM queue WHERE status = 2;";
            return cmd.ExecuteNonQuery();
        }
    }

    public int DeleteDeadItems()
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            using var con = new SqliteConnection(_connString);
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = "DELETE FROM queue WHERE status = 3;";
            return cmd.ExecuteNonQuery();
        }
    }

    public void TrimOldestByCount(int maxRows)
    {
        if (maxRows < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRows));
        ThrowIfDisposed();

        lock (_lock)
        {
            using var con = new SqliteConnection(_connString);
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
DELETE FROM queue
WHERE id IN (
    SELECT id FROM queue
    ORDER BY id ASC
    LIMIT (
        SELECT CASE WHEN (SELECT COUNT(*) FROM queue) > $maxRows
                    THEN (SELECT COUNT(*) FROM queue) - $maxRows
                    ELSE 0 END
    )
);
";
            cmd.Parameters.AddWithValue("$maxRows", maxRows);
            cmd.ExecuteNonQuery();
        }
    }

    public int GetPendingCount()
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            using var con = new SqliteConnection(_connString);
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM queue WHERE status = 0;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public int GetTotalCount()
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            using var con = new SqliteConnection(_connString);
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM queue;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public QueueStats GetStats()
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            using var con = new SqliteConnection(_connString);
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
SELECT 
    COALESCE(SUM(CASE WHEN status = 0 THEN 1 ELSE 0 END), 0) as pending,
    COALESCE(SUM(CASE WHEN status = 1 THEN 1 ELSE 0 END), 0) as leased,
    COALESCE(SUM(CASE WHEN status = 2 THEN 1 ELSE 0 END), 0) as sent,
    COALESCE(SUM(CASE WHEN status = 3 THEN 1 ELSE 0 END), 0) as dead,
    COUNT(*) as total
FROM queue;
";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new QueueStats(
                    Pending: reader.GetInt32(0),
                    Leased: reader.GetInt32(1),
                    Sent: reader.GetInt32(2),
                    Dead: reader.GetInt32(3),
                    Total: reader.GetInt32(4)
                );
            }

            return new QueueStats(0, 0, 0, 0, 0);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SqliteConnection.ClearAllPools();
    }

    private static void TryAddColumn(SqliteConnection con, string table, string column, string definition)
    {
        // Prüfen, ob Spalte existiert
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({table});";
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var name = reader.GetString(1);
                if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                    return; // exists
            }
        }

        // Spalte hinzufügen
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
            cmd.ExecuteNonQuery();
        }
    }
}

public sealed record QueuedItem(
    long Id, 
    string CreatedUtc, 
    string Type, 
    string PayloadJson,
    int RetryCount = 0);

public sealed record QueueStats(
    int Pending, 
    int Leased, 
    int Sent, 
    int Dead, 
    int Total);
