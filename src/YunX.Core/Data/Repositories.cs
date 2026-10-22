using Microsoft.Data.Sqlite;

namespace YunX.Core.Data;

/// <summary>任务持久化状态。</summary>
public enum PersistedTaskState
{
    Queued = 0,
    Running = 1,
    Paused = 2,
    Failed = 3,
}

/// <summary>一条持久化的下载任务。</summary>
public sealed class PersistedTask
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public string SaveDir { get; set; } = "";
    public long TotalBytes { get; set; }
    public long DoneBytes { get; set; }
    public PersistedTaskState State { get; set; }
    public string Error { get; set; } = "";
    public string MirrorName { get; set; } = "";
    /// <summary>附加字段（请求头/平台信息等），JSON 序列化后存放。</summary>
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>解析历史条目。</summary>
public sealed class HistoryEntry
{
    public long Id { get; set; }
    public string RawInput { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>提取码。原项目支持复用提取码，这是它比"只记链接"更实用的地方。</summary>
    public string Passcode { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>链接收藏。</summary>
public sealed class FavoriteEntry
{
    public long Id { get; set; }
    public string RawInput { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UsedAt { get; set; }
    public int UseCount { get; set; }
}

/// <summary>任务仓储。</summary>
public sealed class TaskRepository
{
    private readonly AppDatabase _db;

    public TaskRepository(AppDatabase db) => _db = db;

    public void Upsert(PersistedTask t)
    {
        _db.Execute(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = t.Id > 0
                ? """
                  UPDATE download_task SET total_bytes=$total, done_bytes=$done, state=$state,
                         error=$err, mirror_name=$mirror, payload_json=$payload, updated_at=$upd
                   WHERE id=$id
                  """
                : """
                  INSERT INTO download_task(url, file_name, save_dir, total_bytes, done_bytes,
                         state, error, mirror_name, payload_json, created_at, updated_at)
                  VALUES ($url, $name, $dir, $total, $done, $state, $err, $mirror, $payload, $now, $now)
                  """;

            if (t.Id > 0) cmd.Parameters.AddWithValue("$id", t.Id);
            cmd.Parameters.AddWithValue("$url", t.Url);
            cmd.Parameters.AddWithValue("$name", t.FileName);
            cmd.Parameters.AddWithValue("$dir", t.SaveDir);
            cmd.Parameters.AddWithValue("$total", t.TotalBytes);
            cmd.Parameters.AddWithValue("$done", t.DoneBytes);
            cmd.Parameters.AddWithValue("$state", (int)t.State);
            cmd.Parameters.AddWithValue("$err", t.Error);
            cmd.Parameters.AddWithValue("$mirror", t.MirrorName);
            cmd.Parameters.AddWithValue("$payload", t.PayloadJson);

            // ★ $now 与 $upd 必须**都**绑定。
            //
            //   UPDATE 分支的 SQL 写的是 `updated_at=$upd`，而早期这里只加了 $now
            //   （$now 只有 INSERT 分支用）。结果是：只要 Id > 0 就必然抛
            //   `InvalidOperationException: Must add values for the following parameters: $upd`，
            //   而这个异常又被上层 PersistTask 的空 catch 吞掉 ——
            //   持久化**一次都没成功过**，且零日志零痕迹。
            //
            //   两个都绑上：SQL 里出现过的占位符，参数集合里必须齐全。
            //   Microsoft.Data.Sqlite 是按「SQL 里声明了但参数里没有」来报错的，
            //   反过来（参数多绑了没用上）不会报错，所以多绑 $now 是无害的。
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.Parameters.AddWithValue("$upd", Now());

            cmd.ExecuteNonQuery();
            if (t.Id == 0)
            {
                using var idCmd = conn.CreateCommand();
                idCmd.CommandText = "SELECT last_insert_rowid()";
                t.Id = Convert.ToInt64(idCmd.ExecuteScalar());
            }
        });
    }

    /// <summary>只更新进度（高频调用，字段最少）。</summary>
    public void UpdateProgress(long id, long done, long total)
    {
        _db.Execute(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE download_task SET done_bytes=$done, total_bytes=$total, updated_at=$now
                 WHERE id=$id
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$done", done);
            cmd.Parameters.AddWithValue("$total", total);
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.ExecuteNonQuery();
        });
    }

    public void Delete(long id)
    {
        _db.Execute(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM download_task WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>启动时恢复：只取未完成且未失败的任务。</summary>
    public List<PersistedTask> LoadRestorable()
    {
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, url, file_name, save_dir, total_bytes, done_bytes, state,
                       error, mirror_name, payload_json, created_at, updated_at
                  FROM download_task
                 WHERE state IN (0, 1, 2)
                 ORDER BY created_at DESC
                """;
            using var r = cmd.ExecuteReader();
            var list = new List<PersistedTask>();
            while (r.Read())
            {
                list.Add(new PersistedTask
                {
                    Id = r.GetInt64(0),
                    Url = r.GetString(1),
                    FileName = r.GetString(2),
                    SaveDir = r.GetString(3),
                    TotalBytes = r.GetInt64(4),
                    DoneBytes = r.GetInt64(5),
                    State = (PersistedTaskState)r.GetInt32(6),
                    Error = r.GetString(7),
                    MirrorName = r.GetString(8),
                    PayloadJson = r.GetString(9),
                    CreatedAt = FromUnix(r.GetInt64(10)),
                    UpdatedAt = FromUnix(r.GetInt64(11)),
                });
            }
            return list;
        });
    }

    /// <summary>清理已完成/已取消的记录（它们已交付，持久化没有意义）。</summary>
    public int PruneFinished(TimeSpan olderThan)
    {
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM download_task WHERE updated_at < $cut";
            cmd.Parameters.AddWithValue("$cut", Now() - (long)olderThan.TotalSeconds);
            return cmd.ExecuteNonQuery();
        });
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static DateTimeOffset FromUnix(long s) => DateTimeOffset.FromUnixTimeSeconds(s);
}

/// <summary>解析历史仓储。</summary>
public sealed class HistoryRepository
{
    /// <summary>同链接在该时间窗内不重复记录（原项目是 1 小时去重）。</summary>
    private static readonly TimeSpan DedupWindow = TimeSpan.FromHours(1);
    private const int MaxEntries = 200;

    private readonly AppDatabase _db;

    public HistoryRepository(AppDatabase db) => _db = db;

    public void Add(string rawInput, string kind, string title, string passcode)
    {
        if (string.IsNullOrWhiteSpace(rawInput)) return;

        _db.Execute(conn =>
        {
            long cut = DateTimeOffset.UtcNow.Add(-DedupWindow).ToUnixTimeSeconds();

            // 去重：同链接在窗口内已存在就更新（标题/提取码可能变），不新增
            using (var find = conn.CreateCommand())
            {
                find.CommandText =
                    "SELECT id FROM resolve_history WHERE raw_input=$raw AND created_at >= $cut LIMIT 1";
                find.Parameters.AddWithValue("$raw", rawInput);
                find.Parameters.AddWithValue("$cut", cut);
                var existing = find.ExecuteScalar();

                if (existing != null)
                {
                    using var upd = conn.CreateCommand();
                    upd.CommandText = """
                        UPDATE resolve_history SET kind=$kind, title=$title, passcode=$pwd,
                               created_at=$now WHERE id=$id
                        """;
                    upd.Parameters.AddWithValue("$id", Convert.ToInt64(existing));
                    upd.Parameters.AddWithValue("$kind", kind);
                    upd.Parameters.AddWithValue("$title", title);
                    upd.Parameters.AddWithValue("$pwd", passcode);
                    upd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    upd.ExecuteNonQuery();
                    return;
                }
            }

            using (var ins = conn.CreateCommand())
            {
                ins.CommandText = """
                    INSERT INTO resolve_history(raw_input, kind, title, passcode, created_at)
                    VALUES ($raw, $kind, $title, $pwd, $now)
                    """;
                ins.Parameters.AddWithValue("$raw", rawInput);
                ins.Parameters.AddWithValue("$kind", kind);
                ins.Parameters.AddWithValue("$title", title);
                ins.Parameters.AddWithValue("$pwd", passcode);
                ins.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                ins.ExecuteNonQuery();
            }

            // 只保留最近 N 条
            using var prune = conn.CreateCommand();
            prune.CommandText = """
                DELETE FROM resolve_history WHERE id NOT IN (
                    SELECT id FROM resolve_history ORDER BY created_at DESC LIMIT $max
                )
                """;
            prune.Parameters.AddWithValue("$max", MaxEntries);
            prune.ExecuteNonQuery();
        });
    }

    public List<HistoryEntry> Query(string? keyword, int limit = 100)
    {
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            bool hasKw = !string.IsNullOrWhiteSpace(keyword);
            cmd.CommandText = hasKw
                ? """
                  SELECT id, raw_input, kind, title, passcode, created_at FROM resolve_history
                   WHERE raw_input LIKE $kw OR title LIKE $kw
                   ORDER BY created_at DESC LIMIT $lim
                  """
                : """
                  SELECT id, raw_input, kind, title, passcode, created_at FROM resolve_history
                   ORDER BY created_at DESC LIMIT $lim
                  """;
            if (hasKw) cmd.Parameters.AddWithValue("$kw", $"%{keyword!.Trim()}%");
            cmd.Parameters.AddWithValue("$lim", limit);

            using var r = cmd.ExecuteReader();
            var list = new List<HistoryEntry>();
            while (r.Read())
            {
                list.Add(new HistoryEntry
                {
                    Id = r.GetInt64(0),
                    RawInput = r.GetString(1),
                    Kind = r.GetString(2),
                    Title = r.GetString(3),
                    Passcode = r.GetString(4),
                    CreatedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5)),
                });
            }
            return list;
        });
    }

    public void Delete(long id) => _db.Execute(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM resolve_history WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    });

    public void Clear() => _db.Execute(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM resolve_history";
        cmd.ExecuteNonQuery();
    });
}

/// <summary>收藏仓储。</summary>
public sealed class FavoriteRepository
{
    private readonly AppDatabase _db;

    public FavoriteRepository(AppDatabase db) => _db = db;

    public bool Add(string rawInput, string label)
    {
        if (string.IsNullOrWhiteSpace(rawInput)) return false;
        return _db.Query(conn =>
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO favorite(raw_input, label, created_at) VALUES ($raw, $label, $now)
                    ON CONFLICT(raw_input) DO UPDATE SET label=$label
                    """;
                cmd.Parameters.AddWithValue("$raw", rawInput);
                cmd.Parameters.AddWithValue("$label", label);
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.ExecuteNonQuery();
                return true;
            }
            catch (SqliteException)
            {
                return false;
            }
        });
    }

    public bool Remove(string rawInput) => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM favorite WHERE raw_input=$raw";
        cmd.Parameters.AddWithValue("$raw", rawInput);
        return cmd.ExecuteNonQuery() > 0;
    });

    public bool Exists(string rawInput) => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM favorite WHERE raw_input=$raw LIMIT 1";
        cmd.Parameters.AddWithValue("$raw", rawInput);
        return cmd.ExecuteScalar() != null;
    });

    /// <summary>按使用次数与最近使用排序：常用置顶，符合直觉。</summary>
    public List<FavoriteEntry> List(int limit = 50)
    {
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, raw_input, label, created_at, used_at, use_count FROM favorite
                 ORDER BY use_count DESC, used_at DESC, created_at DESC LIMIT $lim
                """;
            cmd.Parameters.AddWithValue("$lim", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<FavoriteEntry>();
            while (r.Read())
            {
                list.Add(new FavoriteEntry
                {
                    Id = r.GetInt64(0),
                    RawInput = r.GetString(1),
                    Label = r.GetString(2),
                    CreatedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)),
                    UsedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)),
                    UseCount = r.GetInt32(5),
                });
            }
            return list;
        });
    }

    public void MarkUsed(string rawInput) => _db.Execute(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE favorite SET use_count = use_count + 1, used_at = $now WHERE raw_input = $raw
            """;
        cmd.Parameters.AddWithValue("$raw", rawInput);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    });
}

/// <summary>凭证仓储（值经过 <see cref="Security.CredentialCipher"/> 加密）。</summary>
public sealed class CredentialRepository
{
    private readonly AppDatabase _db;
    private readonly Security.CredentialCipher _cipher;

    public CredentialRepository(AppDatabase db, Security.CredentialCipher cipher)
    {
        _db = db;
        _cipher = cipher;
    }

    public void Save(string platform, string value, string extra = "")
    {
        string enc = _cipher.Encrypt(value);
        string encExtra = _cipher.Encrypt(extra);
        _db.Execute(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO credential(platform, value, extra, updated_at)
                VALUES ($p, $v, $e, $now)
                ON CONFLICT(platform) DO UPDATE SET value=$v, extra=$e, updated_at=$now
                """;
            cmd.Parameters.AddWithValue("$p", platform);
            cmd.Parameters.AddWithValue("$v", enc);
            cmd.Parameters.AddWithValue("$e", encExtra);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// 读取凭证；解不开时返回 <c>null</c>。
    ///
    /// <para>解不开最常见的两种情形是<b>换了机器</b>或<b>换了 Windows 用户</b> ——
    /// 这是主密钥绑定策略的**预期行为**，不是数据损坏。上层应把 null 当作
    /// 「未登录」处理，让用户重新登录即可，而不是报"凭证损坏"去引导用户删库。</para>
    /// </summary>
    public (string Value, string Extra)? Load(string platform)
    {
        return _db.Query<(string, string)?>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value, extra FROM credential WHERE platform=$p";
            cmd.Parameters.AddWithValue("$p", platform);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;

            var value = _cipher.Decrypt(r.GetString(0));
            var extra = _cipher.Decrypt(r.GetString(1));
            if (value == null) return null;   // 密钥不匹配
            return (value, extra ?? "");
        });
    }

    public List<string> Platforms() => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT platform FROM credential ORDER BY platform";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    });

    public void Delete(string platform) => _db.Execute(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM credential WHERE platform=$p";
        cmd.Parameters.AddWithValue("$p", platform);
        cmd.ExecuteNonQuery();
    });
}
