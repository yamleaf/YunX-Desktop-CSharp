#if !NET48
using Microsoft.Data.Sqlite;
#endif

namespace YunX.Core.Data;

/// <summary>任务持久化状态。</summary>
public enum PersistedTaskState
{
    Queued = 0,
    Running = 1,
    Paused = 2,
    Failed = 3,

    /// <summary>
    /// 已完成。
    ///
    /// <para><b>保留它是为了"下载历史"</b>：完成的任务不再需要恢复断点，
    /// 但用户想看到"我下过什么"。所以这类行不会被删，只是不参与恢复
    /// （<see cref="TaskRepository.LoadRestorable"/> 按状态过滤掉它）。</para>
    ///
    /// <para>数量由 <see cref="TaskRepository.PruneCompleted"/> 控制在最近 N 条。</para>
    /// </summary>
    Done = 4,

    /// <summary>
    /// 已取消（用户主动放弃，但断点可能还在）。
    ///
    /// <para>与 <see cref="Paused"/> 的区别：暂停是"以后继续"，取消是"不要了"。
    /// 但两者都保留断点，所以都参与恢复 —— 由用户决定。</para>
    /// </summary>
    Cancelled = 5,
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

    /// <summary>首次真正开始传输的时刻（Unix 秒；0 = 还没开始）。</summary>
    public long TransferStartAt { get; set; }

    /// <summary>完成时刻（Unix 秒；0 = 未完成）。</summary>
    public long FinishedAt { get; set; }

    /// <summary>
    /// 累计传输耗时（秒，只算真正在传的时间）。
    ///
    /// <para>单独存一列而不是用 finished - started 相减：暂停、排队、等待用户
    /// 处理同名冲突的时间都不该算进下载耗时，相减会把它算进去。</para>
    /// </summary>
    public long ElapsedSeconds { get; set; }
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

    /// <summary>
    /// 这个 label 是不是用户自己起的。
    ///
    /// <para>true：用户手动命名，任何自动逻辑都不许改它；
    /// false：程序生成的短名（owner/repo 之类），可以随解析结果更新。</para>
    /// </summary>
    public bool LabelCustom { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UsedAt { get; set; }
    public int UseCount { get; set; }

    /// <summary>
    /// 界面上该显示的标题：**自定义名优先，否则显示网址**。
    ///
    /// <para>用户的原始诉求就是这个 —— 自己命名过就用自己的，没有就显示网址，
    /// 不要拿程序猜出来的短名冒充标题（猜错了反而误导）。</para>
    ///
    /// <para>兜底历史脏数据：早期版本会把输入框占位提示「等待输入链接」
    /// 当成标题存进 label 列，这类值一律视为无效。</para>
    /// </summary>
    public string DisplayLabel =>
        LabelCustom && !string.IsNullOrWhiteSpace(Label) && Label != "等待输入链接"
            ? Label
            : ShortUrl(RawInput);

    /// <summary>
    /// 把链接压成便于阅读的短名（只在没有自定义名时用）。
    ///
    /// <para>侧边栏/列表的可见宽度有限，裸链接截断后只剩
    /// `https://github.com/rust-l…`，信息量几乎为零。</para>
    ///
    /// <para>纯字符串处理，不引用 YunX.GitHub 的解析器：
    /// Core 层不该为了显示个标题就依赖上层的链接解析。</para>
    /// </summary>
    private static string ShortUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;

        // magnet: 保留 infohash 前 8 位就够区分了
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            int i = url.IndexOf("btih:", StringComparison.OrdinalIgnoreCase);
            return i >= 0 && url.Length >= i + 13
                ? $"磁力 {url.Substring(i + 5, 8)}…"
                : "磁力链接";
        }

        string s = url;
        foreach (var p in new[] { "https://", "http://", "www." })
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) s = s[p.Length..];

        s = s.TrimEnd('/');

        // github.com/owner/repo/... → owner/repo（后面那段多半是 releases/blob 之类）
        if (s.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            string rest = s["github.com/".Length..];
            var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return $"{parts[0]}/{parts[1]}";
            if (parts.Length == 1) return parts[0];
        }

        return s;
    }
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
                  UPDATE download_task SET file_name=$name, save_dir=$dir,
                         total_bytes=$total, done_bytes=$done, state=$state,
                         error=$err, mirror_name=$mirror, payload_json=$payload,
                         transfer_start_at=$tstart, finished_at=$fin, elapsed_s=$elapsed,
                         updated_at=$upd
                   WHERE id=$id
                  """
                : """
                  INSERT INTO download_task(url, file_name, save_dir, total_bytes, done_bytes,
                         state, error, mirror_name, payload_json,
                         transfer_start_at, finished_at, elapsed_s, created_at, updated_at)
                  VALUES ($url, $name, $dir, $total, $done, $state, $err, $mirror, $payload,
                          $tstart, $fin, $elapsed, $now, $now)
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
            cmd.Parameters.AddWithValue("$tstart", t.TransferStartAt);
            cmd.Parameters.AddWithValue("$fin", t.FinishedAt);
            cmd.Parameters.AddWithValue("$elapsed", t.ElapsedSeconds);

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

    /// <summary>
    /// 启动时恢复：只取**未完成**的任务。
    ///
    /// <para>已完成（Done=4）的行不参与恢复 —— 文件已经在磁盘上了，没什么可"继续"的。
    /// 它们只用于把下载历史显示回列表（见 <see cref="LoadCompleted"/>）。</para>
    ///
    /// <para>已取消（Cancelled=5）**参与**恢复：用户点取消时断点是刻意保留的，
    /// 重启后应该还能"继续"。把它排除掉会让"取消可续传"这个承诺落空。</para>
    /// </summary>
    public List<PersistedTask> LoadRestorable()
    {
        return QueryTasks("state IN (0, 1, 2, 5)", "created_at DESC", 0);
    }

    /// <summary>
    /// 已完成任务的历史记录，按完成时间倒序，最多 <paramref name="limit"/> 条。
    ///
    /// <para>只用于展示，不参与恢复。<paramref name="limit"/> 就是「保留最近 N 条」的 N。</para>
    /// </summary>
    public List<PersistedTask> LoadCompleted(int limit)
    {
        if (limit <= 0) return new List<PersistedTask>();
        // 加 id 作为次级排序：时间戳是秒级的，同一秒内完成多个任务时
        // 光按 updated_at 排会得到不确定的顺序（SQLite 不保证稳定排序）。
        return QueryTasks("state = 4", "updated_at DESC, id DESC", limit);
    }

    private List<PersistedTask> QueryTasks(string where, string orderBy, int limit)
    {
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT id, url, file_name, save_dir, total_bytes, done_bytes, state,
                       error, mirror_name, payload_json, created_at, updated_at,
                       transfer_start_at, finished_at, elapsed_s
                  FROM download_task
                 WHERE {where}
                 ORDER BY {orderBy}
                 {(limit > 0 ? "LIMIT $limit" : "")}
                """;
            if (limit > 0) cmd.Parameters.AddWithValue("$limit", limit);

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
                    TransferStartAt = r.GetInt64(12),
                    FinishedAt = r.GetInt64(13),
                    ElapsedSeconds = r.GetInt64(14),
                });
            }
            return list;
        });
    }

    /// <summary>
    /// 只保留最近 <paramref name="keep"/> 条已完成记录，多出来的按更新<strong>更早</strong>优先删除。
    ///
    /// <para><b>为什么在写入时裁剪而不是启动时</b>：启动时裁剪的话，用户在一次会话里
    /// 下 200 个文件，库会先涨到 200 条再一次性砍；写入时裁剪能让表大小始终收敛。</para>
    ///
    /// <para>返回实际删除的行数。</para>
    /// </summary>
    public int PruneCompleted(int keep)
    {
        if (keep < 0) return 0;
        int deleted = 0;
        _db.Execute(conn =>
        {
            using var cmd = conn.CreateCommand();
            // 删除"不在最近 keep 条之内"的已完成行。
            // 用 id 子查询而不是 OFFSET：SQLite 的 DELETE ... LIMIT 需要特殊编译选项。
            cmd.CommandText = """
                DELETE FROM download_task
                 WHERE state = 4
                   AND id NOT IN (
                       SELECT id FROM download_task
                        WHERE state = 4
                        ORDER BY updated_at DESC, id DESC
                        LIMIT $keep
                   )
                """;
            cmd.Parameters.AddWithValue("$keep", keep);
            deleted = cmd.ExecuteNonQuery();
        });
        return deleted;
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
    /// <summary>
    /// 同链接的去重窗口。
    ///
    /// <para><b>为什么原来是 1 小时、现在改成"无限"</b>：解析历史是「我解析过哪些东西」的清单，
    /// 不是操作日志。同一个仓库隔两小时再解析一次，用户并不希望历史里多出第二条一模一样的记录 ——
    /// 他要的是"这条排到最前面"。</para>
    ///
    /// <para>所以现在：同链接（规范化后）永远只有一条，再次解析只更新时间和标题。</para>
    /// </summary>
    private static readonly TimeSpan DedupWindow = TimeSpan.MaxValue;
    private const int MaxEntries = 200;

    private readonly AppDatabase _db;

    public HistoryRepository(AppDatabase db) => _db = db;

    /// <summary>
    /// 把链接规范化成用于去重的 key。
    ///
    /// <para>用户粘贴同一个仓库可能有多种写法：带不带协议、末尾有无斜杠、有没有 www。
    /// 如果直接拿原始字符串比对，这些都会变成不同的历史条目。</para>
    ///
    /// <para>只做"安全的"归一化：不改动路径语义（<c>/releases</c> 和根路径仍算不同），
    /// 否则会把用户刻意访问的不同页面合并成一条，反而丢信息。</para>
    /// </summary>
    public static string NormalizeKey(string rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput)) return "";

        string s = rawInput.Trim();

        // magnet: 只保留 infohash（忽略 dn= 之类的显示名，它们是可变的）
        if (s.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            int i = s.IndexOf("btih:", StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                string rest = s[(i + 5)..];
                int amp = rest.IndexOf('&');
                if (amp >= 0) rest = rest[..amp];
                return "magnet:" + rest.TrimEnd('/').ToLowerInvariant();
            }
            return s.ToLowerInvariant();
        }

        // 协议与 www 前缀不影响"是同一个东西"
        foreach (var p in new[] { "https://", "http://" })
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { s = s[p.Length..]; break; }
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = s[4..];

        // 末尾斜杠、常见跟踪参数
        s = s.TrimEnd('/');
        foreach (var junk in new[] { "?utm_source=", "?utm_medium=", "?from=" })
        {
            int i = s.IndexOf(junk, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) s = s[..i].TrimEnd('/');
        }

        // 主机名部分统一小写（路径大小写敏感，不能动）
        int slash = s.IndexOf('/');
        return slash > 0
            ? s[..slash].ToLowerInvariant() + s[slash..]
            : s.ToLowerInvariant();
    }

    public void Add(string rawInput, string kind, string title, string passcode)
    {
        if (string.IsNullOrWhiteSpace(rawInput)) return;

        _db.Execute(conn =>
        {
            long cut = DedupWindow == TimeSpan.MaxValue
                ? long.MinValue                       // 窗口无限：任何历史记录都参与去重
                : DateTimeOffset.UtcNow.Add(-DedupWindow).ToUnixTimeSeconds();

            string key = NormalizeKey(rawInput);

            // 去重：同链接已存在就更新（标题/提取码可能变、时间刷新到最前），不新增。
            // 注意比对的是**规范化后的 key**，不是原始字符串 —— 库里存的是原文，
            // 所以这里把候选行全取出来在内存里比（历史最多 200 条，代价可忽略）。
            long? existingId = null;
            using (var all = conn.CreateCommand())
            {
                all.CommandText = "SELECT id, raw_input, created_at FROM resolve_history WHERE created_at >= $cut";
                all.Parameters.AddWithValue("$cut", cut);
                using var r = all.ExecuteReader();
                while (r.Read())
                {
                    if (NormalizeKey(r.GetString(1)) == key)
                    {
                        existingId = r.GetInt64(0);
                        break;
                    }
                }
            }

            if (existingId != null)
            {
                using var upd = conn.CreateCommand();
                upd.CommandText = """
                    UPDATE resolve_history
                       SET raw_input=$raw, kind=$kind, title=$title, passcode=$pwd, created_at=$now
                     WHERE id=$id
                    """;
                upd.Parameters.AddWithValue("$id", existingId.Value);
                upd.Parameters.AddWithValue("$raw", rawInput);
                upd.Parameters.AddWithValue("$kind", kind);
                upd.Parameters.AddWithValue("$title", title);
                upd.Parameters.AddWithValue("$pwd", passcode);
                upd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                upd.ExecuteNonQuery();
                return;
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
                // ★ 冲突时**不能**无条件覆盖 label。
                //   before：DO UPDATE SET label=$label —— 用户重新收藏同一个链接时，
                //   辛苦起的中文名会被自动短名冲掉。
                //   现在：只有「原来不是自定义名」才更新，用户起过的名字保持不变。
                cmd.CommandText = """
                    INSERT INTO favorite(raw_input, label, label_custom, created_at)
                    VALUES ($raw, $label, 0, $now)
                    ON CONFLICT(raw_input) DO UPDATE SET
                        label = CASE WHEN favorite.label_custom = 1
                                     THEN favorite.label
                                     ELSE excluded.label END
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

    /// <summary>
    /// 重命名收藏。传空字符串即「清除自定义名」，显示会退回网址。
    ///
    /// <para>返回 false 表示这条收藏不存在（或库写失败）。</para>
    /// </summary>
    public bool Rename(string rawInput, string newLabel)
    {
        string trimmed = (newLabel ?? "").Trim();
        // 与输入框占位符撞名的按"未命名"处理，否则界面上又是一片「等待输入链接」
        if (trimmed == "等待输入链接") trimmed = "";

        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE favorite
                   SET label = $label, label_custom = $custom
                 WHERE raw_input = $raw
                """;
            cmd.Parameters.AddWithValue("$label", trimmed);
            cmd.Parameters.AddWithValue("$custom", trimmed.Length > 0 ? 1 : 0);
            cmd.Parameters.AddWithValue("$raw", rawInput);
            return cmd.ExecuteNonQuery() > 0;
        });
    }

    public bool Remove(string rawInput) => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM favorite WHERE raw_input=$raw";
        cmd.Parameters.AddWithValue("$raw", rawInput);
        return cmd.ExecuteNonQuery() > 0;
    });

    /// <summary>清空全部收藏。返回删除条数。</summary>
    public int Clear()
    {
        int n = 0;
        _db.Execute(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM favorite";
            n = cmd.ExecuteNonQuery();
        });
        return n;
    }

    public bool Exists(string rawInput) => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM favorite WHERE raw_input=$raw LIMIT 1";
        cmd.Parameters.AddWithValue("$raw", rawInput);
        return cmd.ExecuteScalar() != null;    });

    /// <summary>按使用次数与最近使用排序：常用置顶，符合直觉。</summary>
    public List<FavoriteEntry> List(int limit = 50)
    {
        return _db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, raw_input, label, created_at, used_at, use_count, label_custom
                  FROM favorite
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
                    LabelCustom = r.GetInt32(6) != 0,
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

    /// <summary>
    /// 清空全部凭证。只在「主密钥已重建」后用 ——
    /// 那时表里每条密文都用旧密钥加密，一律解不开，留着只会让每个平台
    /// 读取时各返回一次 null，用户看到的是一堆「未登录」却不知道为什么。
    /// </summary>
    public void ClearAll() => _db.Execute(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM credential";
        cmd.ExecuteNonQuery();
    });
}
