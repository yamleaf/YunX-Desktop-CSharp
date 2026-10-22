using Microsoft.Data.Sqlite;

namespace YunX.Core.Data;

/// <summary>
/// SQLite 数据库：任务持久化、解析历史、链接收藏、凭证。
///
/// <para><b>为什么手写 SQL 而不用 ORM</b>：表结构只有 4 张、查询都是简单 CRUD，
/// 引入 EF Core 会带来 40MB+ 的依赖和一整套迁移机制，
/// 对一个单文件发布的桌面工具不划算。</para>
///
/// <para><b>并发</b>：SQLite 单写多读，这里用一条共享连接 + 锁。
/// 所有调用都在短事务内完成，不持有锁做 IO。</para>
/// </summary>
public sealed class AppDatabase : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public string FilePath { get; }

    public AppDatabase(string dbPath)
    {
        FilePath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };

        _conn = new SqliteConnection(builder.ToString());
        _conn.Open();

        // WAL：读写不互相阻塞，掉电恢复能力也更好
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA foreign_keys=ON;");
        Exec("PRAGMA busy_timeout=5000;");

        Migrate();
    }

    private void Migrate()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS schema_info (
                version INTEGER NOT NULL
            );
            """);

        int version = 0;
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT version FROM schema_info LIMIT 1";
            var v = cmd.ExecuteScalar();
            version = v is null ? 0 : Convert.ToInt32(v);
            if (v is null)
            {
                using var ins = _conn.CreateCommand();
                ins.CommandText = "INSERT INTO schema_info(version) VALUES (0)";
                ins.ExecuteNonQuery();
            }
        }

        if (version < 1) MigrateV1();
        if (version < 1) SetVersion(1);
    }

    private void MigrateV1()
    {
        Exec("""
            -- 下载任务：只存**未完成**的任务（完成后即认为已交付，不再需要重启恢复）
            CREATE TABLE IF NOT EXISTS download_task (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                url           TEXT    NOT NULL,
                file_name     TEXT    NOT NULL,
                save_dir      TEXT    NOT NULL,
                total_bytes   INTEGER NOT NULL DEFAULT 0,
                done_bytes    INTEGER NOT NULL DEFAULT 0,
                state         INTEGER NOT NULL DEFAULT 0,
                error         TEXT    NOT NULL DEFAULT '',
                mirror_name   TEXT    NOT NULL DEFAULT '',
                payload_json  TEXT    NOT NULL DEFAULT '',
                created_at    INTEGER NOT NULL,
                updated_at    INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_task_state ON download_task(state, created_at DESC);

            -- 解析历史：1 小时内同链接去重（在写入时判断）
            CREATE TABLE IF NOT EXISTS resolve_history (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                raw_input   TEXT NOT NULL,
                kind        TEXT NOT NULL DEFAULT '',
                title       TEXT NOT NULL DEFAULT '',
                passcode    TEXT NOT NULL DEFAULT '',
                created_at  INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_history_time ON resolve_history(created_at DESC);

            -- 链接收藏
            CREATE TABLE IF NOT EXISTS favorite (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                raw_input   TEXT NOT NULL UNIQUE,
                label       TEXT NOT NULL DEFAULT '',
                created_at  INTEGER NOT NULL,
                used_at     INTEGER NOT NULL DEFAULT 0,
                use_count   INTEGER NOT NULL DEFAULT 0
            );

            -- 凭证：value 是 CredentialCipher 加密后的 Base64
            CREATE TABLE IF NOT EXISTS credential (
                platform    TEXT PRIMARY KEY,
                value       TEXT NOT NULL,
                extra       TEXT NOT NULL DEFAULT '',
                updated_at  INTEGER NOT NULL
            );
            """);
    }

    private void SetVersion(int version)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE schema_info SET version = $v";
            cmd.Parameters.AddWithValue("$v", version);
            cmd.ExecuteNonQuery();
        }
    }

    private void Exec(string sql)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>在事务内执行一组操作（失败自动回滚）。</summary>
    internal T InTransaction<T>(Func<SqliteConnection, SqliteTransaction, T> body)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                var result = body(_conn, tx);
                tx.Commit();
                return result;
            }
            catch
            {
                try { tx.Rollback(); } catch { /* 连接已断时回滚也会失败 */ }
                throw;
            }
        }
    }

    internal T Query<T>(Func<SqliteConnection, T> body)
    {
        lock (_gate) return body(_conn);
    }

    internal void Execute(Action<SqliteConnection> body)
    {
        lock (_gate) body(_conn);
    }

    /// <summary>
    /// 数据库健康自检与自愈。
    ///
    /// <para>进程被强杀后 WAL 的 <c>-shm</c> 可能损坏，导致下次启动
    /// <c>SQLITE_IOERR_SHMMAP</c> 甚至启动即崩。这里按「先删 -shm，再删 -shm + -wal」两级重试。</para>
    /// </summary>
    public static AppDatabase OpenWithHealing(string dbPath)
    {
        try
        {
            return new AppDatabase(dbPath);
        }
        catch (SqliteException)
        {
            foreach (var suffix in new[] { "-shm", "-shm|-wal" })
            {
                foreach (var s in suffix.Split('|'))
                {
                    try
                    {
                        string f = dbPath + s;
                        if (File.Exists(f)) File.Delete(f);
                    }
                    catch { /* 文件被占用时跳过 */ }
                }
                try
                {
                    return new AppDatabase(dbPath);
                }
                catch (SqliteException) { /* 继续下一级 */ }
            }
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            Exec("PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch { /* 关闭阶段失败不影响退出 */ }
        _conn.Dispose();

        // Pooling=true 时 Close/Dispose 只是把连接还给池，池仍持有 db 文件的句柄 ——
        // 退出后文件仍被占用（用户想删除/搬移数据目录会被拒），-wal 也可能残留。
        // ClearPool 才会真正关闭池内所有物理连接。
        try { SqliteConnection.ClearPool(_conn); } catch { /* ignore */ }
        try { SqliteConnection.ClearAllPools(); } catch { /* ignore */ }
    }
}
