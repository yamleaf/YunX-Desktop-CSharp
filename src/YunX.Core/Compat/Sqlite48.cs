// =====================================================================================
//  SQLite 适配层
//
//  两个目标框架用完全不同的 SQLite 实现，连接字符串语法、枚举、异常类型都不同：
//
//                       net8 (Microsoft.Data.Sqlite)      net48 (System.Data.SQLite)
//    连接串 key         Data Source / Mode / Cache         Data Source / Read Only / ...
//    打开模式           SqliteOpenMode 枚举                 SR 上是布尔属性，无枚举
//    缓存模式           SqliteCacheMode 枚举                SR 上叫 Cache Size，无枚举
//
//  与其在调用点堆 #if，不如把这个差异收敛到一个小工厂里：
//  AppDatabase / BrowserCookieReader 只调用 Sqlite48.BuildConnectionString(...)，
//  两个框架各自实现。
//
//  ★ 为什么要为 net48 换实现而不是让 Microsoft.Data.Sqlite 也支持 net48：
//    Microsoft.Data.Sqlite 8.0 只发 netstandard2.0 目标，理论上 net48 能用，
//    但它依赖 SQLitePCLRaw，需要额外的 Batteries_V2.Init() 且原生库路径
//    在单文件场景下极易出错。System.Data.SQLite 把 interop 打包处理好了，
//    在 .NET Framework 上更省心。
// =====================================================================================

#if NET48
using System;
using System.IO;

namespace YunX.Core.Compat
{
    /// <summary>net48 的 SqliteOpenMode 等价物（仅用于调用点统一写法）。</summary>
    internal enum SqliteOpenMode48
    {
        ReadWriteCreate,
        ReadWrite,
        ReadOnly,
        Memory,
    }

    /// <summary>net48 的 SqliteCacheMode 等价物。</summary>
    internal enum SqliteCacheMode48
    {
        Default,
        Private,
        Shared,
    }

    /// <summary>跨框架的 SQLite 连接串构造。</summary>
    internal static class Sqlite48
    {
        /// <summary>
        /// 构造连接串。
        ///
        /// <para>System.Data.SQLite 的连接串语法：
        /// <c>Data Source=path;Read Only=True;Cache Size=8000;Pooling=True</c>。
        /// 「共享缓存」在这里对应 <c>Shared Cache</c> 前缀（<c>file:path?cache=shared</c>），
        /// 需要 URI 形式；为简单起见，net48 固定用 Private 缓存 + WAL —— 
        /// WAL 模式下「读写不互相阻塞」的目标本来就达到了，
        /// Shared 缓存带来的收益在本项目（单进程、共享连接 + 锁）几乎为零。</para>
        /// </summary>
        public static string Build(string dbPath, SqliteOpenMode48 mode, bool pooling = true,
            SqliteCacheMode48 cache = SqliteCacheMode48.Default)
        {
            if (dbPath == null) throw new ArgumentNullException(nameof(dbPath));

            bool readOnly = mode == SqliteOpenMode48.ReadOnly;
            bool inMemory = mode == SqliteOpenMode48.Memory;

            var sb = new System.Text.StringBuilder();
            sb.Append("Data Source=").Append(inMemory ? ":memory:" : dbPath);
            if (readOnly) sb.Append(";Read Only=True");
            if (!readOnly && !inMemory && mode == SqliteOpenMode48.ReadWriteCreate)
                sb.Append(";FailIfMissing=False");
            else if (!inMemory && !readOnly && mode == SqliteOpenMode48.ReadWrite)
                sb.Append(";FailIfMissing=True");

            sb.Append(";Pooling=").Append(pooling ? "True" : "False");
            // 缓存大小：负数 = KB 单位，-8000 约等于 8MB（与 Microsoft.Data.Sqlite 默认接近）
            sb.Append(";Cache Size=-8000");
            sb.Append(";Journal Mode=WAL");
            sb.Append(";Synchronous=Normal");
            return sb.ToString();
        }
    }

    /// <summary>
    /// SQLite 异常判断的跨框架包装。
    ///
    /// <para>两个实现的异常类型不同（SqliteException vs SQLiteException），
    /// 但都暴露 <c>SqliteErrorCode</c>。项目里只用它区分「SQLITE_BUSY（5）」
    /// 与「唯一约束冲突（19）」两种情况，这里统一成一个 helper。</para>
    /// </summary>
    internal static class SqliteErrors
    {
        /// <summary>SQLITE_BUSY：数据库被占用（WAL 下重试即可）。</summary>
        public const int Busy = 5;

        /// <summary>SQLITE_CONSTRAINT：唯一约束/外键约束冲突。</summary>
        public const int Constraint = 19;

        public static bool IsBusy(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is System.Data.SQLite.SQLiteException se && se.ResultCode ==
                    System.Data.SQLite.SQLiteErrorCode.Busy) return true;
            }
            return false;
        }

        public static bool IsConstraint(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is System.Data.SQLite.SQLiteException se && se.ResultCode ==
                    System.Data.SQLite.SQLiteErrorCode.Constraint) return true;
            }
            return false;
        }
    }
}
#endif
