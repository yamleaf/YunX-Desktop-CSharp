// =====================================================================================
//  SQLite 的类型别名
//
//  两个目标框架用不同的 SQLite 实现：
//    · net8  → Microsoft.Data.Sqlite（轻量、无 GAC 依赖、与 .NET 8 同步演进）
//    · net48 → System.Data.SQLite（官方托管包装，原生 interop 由 NuGet 包自动带入）
//
//  两者的 API 面（Connection / Command / Parameter / Transaction / Reader）
//  在项目用到的范围内完全一致，只是命名空间和类名前缀不同
//  （SqliteConnection vs SQLiteConnection）。用 global using 别名拉平。
//
//  ★ 为什么 net8 也要在 else 分支里写一遍：
//    global using 必须无条件下可见，否则同一份源码在两个 TFM 下会走不同的
//    类型解析路径。把别名写在 else 里，等于给 net8 也加了一层间接，
//    好处是「调用点零条件编译」—— 业务代码只写 SqliteConnection，
//    永远不用关心 OS/TFM。
// =====================================================================================

#if NET48
global using SqliteConnection = System.Data.SQLite.SQLiteConnection;
global using SqliteCommand = System.Data.SQLite.SQLiteCommand;
global using SqliteParameter = System.Data.SQLite.SQLiteParameter;
global using SqliteTransaction = System.Data.SQLite.SQLiteTransaction;
global using SqliteDataReader = System.Data.SQLite.SQLiteDataReader;
global using SqliteException = System.Data.SQLite.SQLiteException;
global using SqliteConnectionStringBuilder = System.Data.SQLite.SQLiteConnectionStringBuilder;
#endif
