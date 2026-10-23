# YunX Desktop v1.4.0

**Windows 高速下载器 · C# / WPF 重构版**

这一版只有一个主题：**换掉运行时基础，把 exe 从 70 MB 压到 3.7 MB**。
功能、下载速度、界面都跟 v1.3.0 一致，变的是分发形态。

`Windows 10/11 x64` · 单文件 **3.7 MB** · 需系统自带 .NET Framework 4.8 · AGPL-3.0

---

## 下载

| 文件 | 说明 |
|---|---|
| `YunX-Desktop.exe` | 主程序。双击即用，**3.7 MB** |
| `LICENSE` | AGPL-3.0 协议全文 |

发布目录就这两个文件。没有 `x64\SQLite.Interop.dll`，也没有 `.exe.config`。

配置与历史存在 `%APPDATA%\YunX-Desktop-CS\`，不跟 exe 走。
**从 v1.3.0 升级可直接覆盖 exe**，配置与数据库自动迁移。

---

## 为什么换到 .NET Framework 4.8

v1.3.0 是 net8 自包含单文件，**70 MB**。那个体积基本全是运行时本身 ——
真正的业务代码只有 3 MB 出头。

而目标平台是 Win10 1903+ / Win11，**这些系统出厂就带 .NET Framework 4.8**。
也就是说，为了兼容极少数没装运行时的老机器，让所有人都多下载 66 MB。

### 实测：net48 不比 net8 慢

| 场景 | net8 | net48 |
|---|---|---|
| 慢速宽带 8MB / 8 线程 | 5.28 MB/s | **6.98 MB/s（+32%）** |
| 本地回环 16MB / 8 线程 | 29.55 MB/s | **173.68 MB/s** |
| 32 线程 × 256KB + 分片校验 | 28.1 MB/s | **125.0 MB/s** |

原因不复杂：两边底层 IO/HTTP 都在 350–3200 MB/s 量级，而真实下载只跑 5–28 MB/s，
**瓶颈在引擎调度，不在运行时**。net48 那套 `WriteFile + OVERLAPPED` 的定位写
反而少了一层托管封装。完整分析见 [`SIZE-OPTIMIZATION.md`](SIZE-OPTIMIZATION.md) 第 5.4 节。

### 唯一的能力差异

| 能力 | net8 | net48 |
|---|---|---|
| HTTP/2 | ✅（默认关） | ❌ 最高 1.1 |
| 其余全部功能 | ✅ | ✅ |

HTTP/2 在本项目里本来就默认关闭（部分镜像的 H2 实现有问题），实际无差异。
hosts 覆盖 / DoH 绕过在 net48 上用「URI 换 IP + 补 Host 头 + 按原域名校验 CN/SAN」
实现，效果等价。

---

## 怎么做到单文件

### 托管依赖：Costura.Fody 内嵌

System.Text.Json、WebView2 托管层等全部压进 exe 资源，启动时从内存加载。
常规做法，没有坑。

### SQLite：不能走 Costura，只能自解压

`System.Data.SQLite` 有两处硬性要求磁盘路径，缺一不可：

1. 它内部用 `Path.Combine(Assembly.Location 所在目录, ...)` 算路径。
   从内存资源加载时 `Location` 是空串，直接抛
   `ArgumentNullException（参数名: path1）` —— 栈里只看到 `SQLiteConnection..ctor`，
   完全联想不到是加载方式的问题。
2. 原生库路径是它自己拼的：`<程序集目录>\x64\SQLite.Interop.dll`，拼完直接
   `LoadLibrary`。**不走搜索路径** —— 加 PATH / `AddDllDirectory` 实测无效。

所以这两个文件作为嵌入资源打进 exe，启动最早期（早于任何 SQLite 调用）
由 `NativeBootstrapper` 解压到：

```
%LOCALAPPDATA%\YunX-Desktop-CS\native\<内容指纹>\
    System.Data.SQLite.dll
    x64\SQLite.Interop.dll
```

再用 `Assembly.LoadFrom` 从那里加载托管 dll（于是 `Location` 有值、
原生库就在它旁边的 `x64\`），并 `LoadLibraryEx` 预加载原生 dll 双保险。

目录名用**内容指纹**而不是版本号：换了构建就自动换目录，
不会出现「新 exe 用到旧 dll」这种极难查的问题。

### .exe.config：直接删掉

原本发布目录还会多一个 `YunX-Desktop.exe.config`，里面只有两段，实测都不需要：

- `<supportedRuntime sku=".NETFramework,Version=v4.8" />`
  —— CLR 选择运行时看的是程序集元数据里的 `TargetFrameworkAttribute`，
  exe 里已经带了。这一段只影响「缺框架时弹不弹下载提示框」，
  而目标平台系统自带 4.8，这个兜底没有受众。
- `<bindingRedirect Unsafe 0.0.0.0-6.0.0.0 → 6.0.0.0 />`
  —— Unsafe 已被 Costura 内嵌，发布目录没有第二个副本可冲突。

验证方式：把 `.exe.config` 整个删掉，`--cryptoprobe` 4/4 通过、
`--accountsdiag` 9 项账号全加载、GUI 正常。

---

## 构建

**默认目标改为 net48**：

```bat
build.bat            :: ★默认★ net48 → publish\net48\（3.7 MB）
build.bat net8       :: net8 自包含 → publish\net8\（70 MB）
build.bat test       :: 编译 + 跑全部离线断言（默认 net48）
build.bat test net8  :: net8 + 断言
build.bat debug      :: 只编译 Debug
build.bat clean      :: 清理所有 bin / obj / publish
```

编译仍然需要 .NET 8 SDK，只是产物运行时不依赖它。

net8 版保留着 —— 万一要支持 Win7/8，`build.bat net8` 就能出老系统的包。

---

## 验证

离线断言双框架各跑一遍，**net8 298/298、net48 298/298**。
断言全部用本地 `TcpListener` 模拟 HTTP 服务，不依赖外网。

新增两条断言守住这次的配置：

- Costura 排除清单里有 SQLite 托管 dll 与原生 interop
- net48 不生成 `.exe.config`（三个开关都在）

---

## 变更清单

- 运行时基础 net8 自包含 → net48 单文件，产物 70 MB → **3.7 MB**
- 新增 `NativeBootstrapper`：SQLite 两个文件嵌入 + 启动自解压
- 发布目录不再包含 `x64\SQLite.Interop.dll` 与 `YunX-Desktop.exe.config`
- `build.bat` 默认目标改为 net48，新增 `net8` 参数；修正旧版参数解析里
  `MODE` 先被覆盖、导致 net48 产物落到 `publish\` 并覆盖 net8 的问题
- 发布后检查目录里是否有 exe/LICENSE 之外的文件（Costura 没织入的哨兵）
- 版本号 1.3.0 → 1.4.0
