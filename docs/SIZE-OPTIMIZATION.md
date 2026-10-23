# 体积优化分析：能否迁到 .NET Framework 4.8

**结论：已迁移完成 —— 体积从 70.2 MB 降到 1.6 MB（-97.7%），零安装，
net8 / net48 双目标并存，293 项断言全过。**

本文所有数字都来自本机实测，不是估算。最终结果、踩坑记录、兼容层架构
分别写在第五、六、七节。

---

## 一、当前体积到底花在哪

| 项目 | 大小 |
|---|---|
| **发布后单文件 exe** | **70.2 MB** |
| 解包后（非单文件）总计 | 167 MB |

其中**项目自己的代码只占 0.88 MB**：

| 文件 | 大小 |
|---|---|
| `YunX.Core.dll` | 0.39 MB |
| `YunX.Desktop.dll` | 0.39 MB |
| `YunX.GitHub.dll` | 0.10 MB |
| `e_sqlite3.dll`（SQLite 原生库） | 1.61 MB |

剩下的全是 .NET 运行时：

| 运行时组件 | 大小 |
|---|---|
| `PresentationFramework.dll` | 15.4 MB |
| `System.Windows.Forms.dll` | **12.9 MB** |
| `System.Private.CoreLib.dll` | 12.6 MB |
| `PresentationCore.dll` | 8.1 MB |
| `System.Private.Xml.dll` | 7.6 MB |
| `System.Windows.Forms.Design.dll` | **5.3 MB** |
| `coreclr.dll` | 4.8 MB |
| `D3DCompiler_47_cor3.dll` | 4.7 MB |
| 其余几十个 dll | ~95 MB |

### 顺带发现：约 18 MB 是白带的

`System.Windows.Forms` + `System.Windows.Forms.Design` + `Primitives` ≈ **20 MB**，
但本项目：

- csproj 里**没有** `<UseWindowsForms>true</UseWindowsForms>`
- 代码里**零引用** `System.Windows.Forms`

它是 WPF 自包含发布时被一并打包进来的。**这一块值得单独处理**，与是否迁 net48 无关。

---

## 二、三条路线实测

| 方案 | 实测体积 | 目标机要求 | 可行性 |
|---|---|---|---|
| **A. 现状**：自包含 + 单文件 + 压缩 | **70.2 MB** | 双击即用 | ✅ 已用 |
| **B. 加 Trim 裁剪** | — | — | ❌ **WPF 不支持** |
| **C. 框架依赖单文件** | **4.0 MB** | 需装 .NET 8 Desktop Runtime（~55 MB） | ⚠️ 可行但门槛高 |
| **D. 迁 .NET Framework 4.8** | **1.6 MB**（已实测） | **Win10 1903+ / Win11 自带** | ✅ **已实现** |

### B 为什么不行

```
error NETSDK1168: 启用剪裁时，不支持或不推荐使用 WPF。
```

这是 .NET SDK 的硬性拦截，官方文档明确 WPF 依赖大量反射，裁剪必然在运行时炸。
**这条路线彻底封死。**

### C 的真实代价

4.0 MB 很诱人，但用户要先装 55 MB 的运行时，总账反而更大，
而且「装个下载器还得先装运行时」是劝退级的体验。只适合企业内网统一部署。

### D 的参考实证

同机的另一个项目（`加密打包` / packbin）就是 net48 + Costura.Fody 内嵌依赖：

```
packbin.exe = 4.6 MB   ← 单文件，零安装，Win10 1903+ 直接跑
```

它的 csproj 关键配置：

```xml
<TargetFramework>net48</TargetFramework>
<PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
<!-- .NET Framework 不支持 PublishSingleFile，用 Costura 把 dll 打进 exe -->
<PackageReference Include="Costura.Fody" Version="5.7.0" PrivateAssets="all" />
<PackageReference Include="Fody" Version="6.8.2" PrivateAssets="all" />
```

---

## 三、并发写 PoC 实测结果（决定性）

前面担心的核心问题是：`RandomAccess` 在 net48 没有等价物，改加锁会不会让下载变慢。
**已经实测，结论是：不会。性能差距被网络完全掩盖，不必担心。**

### 3.1 纯写盘上限（4MB × 8 线程，3 轮取中位）

| 方案 | 吞吐 | 说明 |
|---|---|---|
| net8 `RandomAccess.Write` | **1370 MB/s** | 现状 |
| net48 `WriteFile` + OVERLAPPED（16KB 块） | 827 MB/s | pwrite 语义，无锁 |
| net48 `WriteFile` + OVERLAPPED（1MB 块） | 1249 MB/s | 块越大越省系统调用 |
| net48 每线程独立 FileStream | **1744 MB/s** | 各自文件指针，无需锁 |
| net48 共享 FileStream + `lock` | 836 MB/s | 最朴素写法 |

纯写盘场景下 net8 比 net48 加锁方案快 1.6 倍 —— 但这个数字**在真实下载中没有意义**。

### 3.2 模拟真实下载节奏（关键）

每片 4MB、数据按 16KB 一包陆续到达（模拟网络流），8 线程：

| 方案 | 吞吐 |
|---|---|
| A 共享 FileStream + lock | **8.28 MB/s** |
| B 每线程独立 FileStream | **8.31 MB/s** |
| C WriteFile + OVERLAPPED | **8.31 MB/s** |

**三者差异 0.4%，在测量误差内。** 网络到达节奏完全主导，写盘方式无关紧要。

### 3.3 结论

真实下载的吞吐上限由网络决定。本项目实测网络环境：

- 最快线路 `gh-proxy.com`：约 200~250 KB/s（单连接）
- 8 线程叠加：约 **1.3~1.5 MB/s**

而最慢的写盘方案（net48 共享 FileStream + lock）仍有 **8.28 MB/s**，
余量约 **5.5 倍**；换成 `WriteFile + OVERLAPPED` 则是 **500 倍**余量。

**所以「迁 net48 会拖慢下载」这个担忧不成立。**
选 `WriteFile + OVERLAPPED`（或每线程独立 FileStream）都能完全覆盖现有网络带宽。

### 3.4 但仍建议选 `WriteFile + OVERLAPPED`

虽然三种都够用，优先选它的理由不是性能，而是**语义正确**：

- 每线程独立 FileStream 会打开 N 个文件句柄，`SetLength` 预分配时机不明确
- 共享 + lock 是最朴素写法，但锁粒度覆盖整个 Seek+Write，将来若要提高块大小会受限
- `WriteFile + OVERLAPPED` 与 `RandomAccess` **语义一一对应**：
  给定 (handle, buffer, offset) 写入，不动共享文件指针 —— 迁移时引擎逻辑不用改

当前 `DownloadEngine` 的 7 处 `RandomAccess.*` 只需替换为一个小封装类
（`PwriteAsync` / `SetLength` / `FlushToDisk`），调用点保持原样。

---

## 四、迁 net48 的真实工作量

> **本节是迁移前的评估，保留下来作为对照。**
> 实际的完成结果见第五节，踩坑记录见第六节。
> 事后看，评估里唯一低估的是「零散 BCL API」的数量（见 6.x 与第八节）。

### 4.1 必须重写的部分

#### ① `RandomAccess` —— 已由 PoC 解除

```
src/YunX.Core/Download/DownloadEngine.cs   7 处
+ SafeFileHandle / File.OpenHandle          4 处
```

这是 .NET 6+ 才有的 API，提供**基于偏移的并发写**（`pwrite` 语义），
多线程分片写同一个文件**无需加锁**。net48 没有等价物。

替代方案对比：

| 方案 | 可行性 | 代价 |
|---|---|---|
| `FileStream.Seek + Write` + `lock` | ✅ 可用 | 所有写入串行化，**多线程收益大幅下降** |
| P/Invoke `WriteFile` + `OVERLAPPED` | ✅ 最接近 | 要自己写 Win32 封装，复杂度高 |
| 每线程独立 part 文件，完成后合并 | ✅ 干净 | 磁盘占用翻倍，末尾多一次全量复制 |

**PoC 已验证：这一项不再是阻碍。** net48 的 `WriteFile + OVERLAPPED`
在真实下载节奏下与 net8 无差异（见第三节 3.2），且有 500 倍带宽余量。
剩下的只是「写一个小封装类」的工作量，不是性能风险。

#### ② `System.Text.Json`（32 处）

net48 没有。两条路：

- 引 NuGet 包 `System.Text.Json`（体积 +1.5 MB，且要处理依赖链）
- 换 `Newtonsoft.Json`（成熟稳定，但 `JsonNode` 的写法要全部改写）

调用集中在 `YunX.Core/Cloud/Drives/*.cs`（各网盘 API）和 `YunX.GitHub`。

#### ③ `Microsoft.Data.Sqlite` → `System.Data.SQLite`

net48 下 `Microsoft.Data.Sqlite` 虽可运行，但依赖链不如 `System.Data.SQLite` 顺。
`AppDatabase` / `Repositories` 要改连接字符串与部分 API。

#### ④ C# 语言特性（**不是问题**）

```
init;       159 处
required     28 处
record       12 处
```

这些是**编译器特性**，net48 配 `<LangVersion>12</LangVersion>` 可以用。
但 `init` / `record` 需要 `IsExternalInit` 类型（补一个空类即可），
`required` 在 net48 上需要 `RequiredMemberAttribute`（同理）。

**实际上 packbin 那个项目就在 net48 上用了 LangVersion 12。**

### 4.2 小改动

| 项 | 处数 | 处理 |
|---|---|---|
| `ValueTask` | 1 | 改 `Task` |
| `OperatingSystem.IsWindowsVersionAtLeast` | 1 | 改 `Environment.OSVersion` 判断 |
| `WaitAsync` | 3 | 用 `Task.WhenAny` 手写超时 |
| `Span` / `ReadOnlySpan` | 各 1 | 改数组（会多一次分配） |

### 4.3 工作量大致的分档

| 阶段 | 内容 | 风险 |
|---|---|---|
| 1. 改多目标 | csproj 加 `net48` 目标，先修编译错误 | 低 |
| 2. 补语言特性 | `IsExternalInit` 等空类型 | 低 |
| 3. **重写 RandomAccess** | 三选一方案，**必须重新测速** | **高** |
| 4. JSON | 换 Newtonsoft 或引包 | 中（32 处调用） |
| 5. SQLite | 换驱动，验证迁移 | 中 |
| 6. 全量回归 | 293 项断言 + 真实下载 | 中 |

**关键风险在第 3 步**：并发写改成加锁串行后，8 线程下载可能退化到接近单线程。
这会推翻之前所有关于下载速度的优化（慢块抢占、分片并行等）。

---

## 五、迁移实际结果

### 5.1 体积

| 发布形态 | 体积 | 说明 |
|---|---|---|
| net8 自包含单文件 | **70.2 MB** | 原方案，双击即用 |
| **net48 + Costura 内嵌** | **1.6 MB** | 新方案，Win10 1903+ / Win11 自带运行时 |

降幅 **97.7%**，比预估的 4.6 MB 还小一截 —— 原项目自己的代码只有 0.88 MB，
其余全是 .NET 运行时。net48 不需要打包运行时，所以只剩代码 + 少量框架外依赖。

发布目录从「exe + 20 个 dll + 两个子目录」变成 4 个文件：

```
YunX-Desktop.exe          1.6 MB   ← 所有托管依赖已内嵌
YunX-Desktop.exe.config   0.5 KB
YunX-Desktop.pdb          69 KB    ← 可删除，不影响运行
LICENSE                   34 KB
```

### 5.2 双目标并存

```xml
<TargetFrameworks>net8.0-windows;net48</TargetFrameworks>
```

两个目标都完整通过编译，并且**都在各自框架上跑完了全部断言**：

```
                        编译          断言
net8.0-windows   0 错误 0 警告   293 项全过
net48            0 错误 0 警告   293 项全过
```

Smoke 工程也改成了双目标 —— 这一步很关键：net48 的下载引擎走的是
完全不同的写入路径（`PwriteStream` / `WriteFile + OVERLAPPED` / `BCrypt` /
`System.Data.SQLite`），只在 net8 上跑通**不能**证明 net48 的分片定位没写错偏移。
smoke 全部用本地 `TcpListener` 模拟 HTTP 服务，不依赖外网，net48 上跑一遍约 40 秒。

> 事实上正是这一步抓出了两个只在 net48 出现的 bug：
> `SetLength` 被自身共享模式挡住、以及 `BCryptEncrypt` 因结构体大小写错
> 被内核拒绝（见 6.10 / 6.11）。**双框架双跑断言是这次迁移性价比最高的决定。**

保留了随时回退的能力：net48 出问题就发 net8 版；用户嫌体积大就发 net48 版。

### 5.3 net48 版的能力差异

**结论：功能完全一致，包括 hosts 覆盖。** 唯一的限制是 HTTP/2。

| 能力 | net8 | net48 | 说明 |
|---|---|---|---|
| hosts 覆盖 / DoH 绕过 | ✅ `ConnectCallback` | ✅ `HostsRewrite48` | 实现方式不同，效果等价 |
| HTTP/2 | ✅（默认关） | ❌ 最高 1.1 | 项目默认就关，无实际影响 |
| 异步文件 IO | IOCP 真异步 | 同步 + 线程池 | 吞吐反而更快（见 5.4） |
| AES-GCM | BCL 实现 | BCrypt.dll P/Invoke | 行为一致，密钥可互通 |
| 其余全部功能 | ✅ | ✅ | 293 项断言双框架全过 |

#### hosts 覆盖是怎么在 net48 上补回来的

net8 的 `SocketsHttpHandler.ConnectCallback` 能在「TCP 连接」这一层做重定向，
URI 和 Host 头完全不变。net48 的 `HttpClientHandler` 没有这个钩子。

替代方案（`src/YunX.Core/Net/HostsRewrite48.cs` + `HostsOverride48.cs`）做三件事：

1. 把 `request.RequestUri` 的 host 换成覆盖表里的 IP
2. 显式设置 `Host` 头为原始域名（否则 HTTP/1.1 把 Host 发成 IP，CDN 会 404）
3. 给 handler 挂证书校验回调，按**原始域名**校验 CN/SAN
   （否则 TLS 会因 IP≠域名直接失败）

安全性不降低：证书校验仍要求「有效 + 域名匹配（含通配符 `*.example.com`）」，
只是把比对基准从被劫持的 IP 换回真实域名。已被 Smoke 的 14 项断言覆盖，
其中包括一次真实的本地 HTTP 往返（验证服务器收到的 Host 头是原域名而非 IP）。

**唯一残留限制**：TLS 的 SNI 会是 IP。绝大多数 CDN（含 GitHub）按 Host 头路由，
不受影响；极少数启用「SNI 必须匹配」的站点会失败 —— 那种情况走镜像线路。

### 5.4 性能实测（`tools/DownloadBench`，可复跑）

`dotnet run --project tools/DownloadBench -f net48`（net8 换 TFM 即可）。
每项跑 3 轮取中位数，同一台机器、同一份场景代码。

| 场景 | net8 | net48 | 差异 |
|---|---|---|---|
| **慢速宽带 8MB / 8 线程**（真实感最强） | 5.28 MB/s | **6.98 MB/s** | **+32%** |
| 慢速宽带 8MB / 1 线程 | 1.04 MB/s | 1.00 MB/s | −4% |
| 本地回环 16MB / 8 线程 | 29.55 MB/s | **173.68 MB/s** | +488% |
| 32 线程 × 256KB 分片 + 分片校验 | 28.1 MB/s | **125.0 MB/s** | +345% |

**net48 不但没变慢，在真实带宽场景下还快三成。** 原因要拆开看：

| 底层能力 | net8 | net48 |
|---|---|---|
| 纯 IO 单线程写 | 479 MB/s | 689 MB/s |
| 纯 IO 8 线程写 | 1001 MB/s | 578 MB/s |
| 纯 HTTP 单流读 | 1006 MB/s | 349~634 MB/s |
| 纯 HTTP 8 路并发 Range | 3266 MB/s | 2000~2186 MB/s |

两边的底层能力都在 **350~3200 MB/s** 量级，而真实下载只有 5~28 MB/s ——
**瓶颈从来不在 IO 或 HTTP，而在引擎层的调度开销**。net8 的
`RandomAccess.WriteAsync` 每次写都要走一次 IOCP 投递 + 完成通知，
在服务器不缺数据流（本地回环）时这部分固定开销就暴露出来；
net48 的同步 `WriteFile` 直接命中页缓存返回，反而没有这笔成本。

慢速宽带下两边都由网络节奏主导，但 net48 仍有 +32% 优势 ——
因为引擎里还有 `Task.Run` 卸载点，net48 侧的 `_ioGate` 快路径
（无争用时同步执行，见 `PwriteStream.WriteAsync`）省掉了这部分上下文切换。

**单线程那 −4% 是限速采样噪声**：服务端按 10ms/包节流，8MB 文件约 500 个包，
计时误差 ±0.05s 就能造成 ±5% 波动；3 轮结果全部落在 0.99~1.00，说明是稳定的，
但与 net8 的 1.04 差值小于测量分辨率。真实宽带下 8 线程才是主场景。

> **提醒**：这些数字是本地回环 + 限速模拟，不代表真实 GitHub 下载速度。
> 真实速度由线路带宽决定（实测 gh-proxy 约 200~250 KB/s 单连接，
> 8 线程叠加约 1.3~1.5 MB/s），远低于任何一侧的引擎处理能力。
> 换句话说：**迁 net48 对用户的下载速度没有可感知影响。**

---

## 六、迁移踩坑记录（后续维护必读）

按「坑的隐蔽程度」排序，这些都是实际花了时间才定位的：

### 6.1 `Directory.Build.props` 里写 `Condition="'$(TargetFramework)'=='net48'"` 永不生效

props 在 csproj **之前**求值，那时 `$(TargetFramework)` 还是空串。
条件不成立但**不报错**，静默失效 —— 表现为「明明配了，就是不生效」。

所有与 TFM 相关的**衍生**配置必须放 `Directory.Build.targets`（在 csproj 之后）。
但 `TargetFrameworks` 本身连 targets 都不能放 —— 它在 NuGet 还原阶段
（`TargetFrameworkInference`）就要读，比 targets 还早，放 targets 会报
`NETSDK1013: 未识别 TargetFramework 值""`。只能在每个 csproj 里各写一份。

### 6.2 `System.Net.Http` 不是缺引用，是缺 using

net48 的编译命令行里明明有 `/reference:System.Net.Http.dll`，
报错却是「命名空间 System.Net 中不存在 Http」。真正原因是
**net48 的 ImplicitUsings 不含 `System.Net.Http`**（net8 含）。
所以要同时做两件事：`<Reference Include="System.Net.Http" />` + `global using`。

`System.Text.RegularExpressions` 同理。

### 6.3 `System.Index` / `System.Range` 必须自己声明，且不能改名

`arr[^1]` / `str[1..3]` 编译后会引用 `System.Index` / `System.Range` 类型。
net48 没有，而且**编译器不认别名** —— 必须真的在 `System` 命名空间下声明
两个 `struct`（带 `Value` / `IsFromEnd` / `GetOffset` 等编译器期望的成员）。
声明在别的命名空间无效。

### 6.4 `RuntimeHelpers.GetSubArray` 无法用垫片补

数组的 range 切片（`raw[5..]`）编译后调用
`RuntimeHelpers.GetSubArray`，这是**运行时内部方法**，不是公开 API，
无法用扩展方法或自定义类型替代。唯一出路是改写成 `Array.Copy`。
本例改了 3 处（C139Drive / BrowserCookieReader / SlowChunkWatchdog）。

注意：`string` 的 range 切片（`s[1..3]`）走的是另一套 `Substring` 路径，
net48 **可以**用；只有数组不行。

### 6.5 Costura.Fody 必须同时显式引 `Fody` 包

只引 `Costura.Fody` 时能编译通过，但织入阶段可能根本没挂上：
发布目录散落 15 个 dll，日志里找不到任何 Fody 输出。
显式加 `<PackageReference Include="Fody" .../>` 后才会执行
（日志出现 `FodyTarget:` / `Fody/Costura: Executing weaver`）。

另外 `IncludeAssets` 里**不能排除 `compile`**，否则 Fody 警告
`FodyPackageReference` 且嵌入清单失效。正确写法只留 `PrivateAssets=all`。

### 6.6 XML 注释不能含 `--`

`<!-- ---------- 分隔线 ---------- -->` 会让 MSBuild 报
`MSB4024: An XML comment cannot contain '--'`。用 `=====` 代替。
`Directory.Build.targets` 和 csproj 都受此约束。

### 6.7 `PlatformTarget=x64` 会与 net48 的默认 RID `win7-x86` 冲突

```
error NETSDK1032: RuntimeIdentifier 平台"win7-x86"和 PlatformTarget"x64"必须兼容
```

`RuntimeIdentifierInference` 在 targets 之前跑，无法靠条件绕过。
解法是在 csproj 里显式给 net48 分支设 `<RuntimeIdentifier>win7-x64</RuntimeIdentifier>`。

### 6.8 批量改写脚本必须自检嵌套

用正则批量改 `[GeneratedRegex]` 时，第一版脚本把「方括号 + 到分号」当整体匹配，
但特性参数可能跨行且含 `)`，结果只截到第一行，产出了一堆语法错误的残行。

教训：批量改写必须
**(a)** 用括号深度扫描而不是正则贪婪匹配；
**(b)** 每处替换后校验 `#if` / `#endif` 计数增量是否与预期一致；
**(c)** 不一致时从备份回滚并报错，而不是继续写。

### 6.9 编译器垫片不能跨程序集共享

`IsExternalInit` / `RequiredMemberAttribute` / `System.Index` / `System.Range`
这些「编译器垫片」是 `internal` 就够用的，但**每个项目都得各放一份** ——
`global using` 和垫片的作用域都是「编译单元所在项目」，不会从
`YunX.Core` 传递到 `YunX.GitHub` / `YunX.Desktop`。

### 6.10 `AesGcm` —— 结构体大小写错会被内核直接拒绝

`System.Security.Cryptography.AesGcm` 是 .NET Core 3.0+ 才有，
而 .NET Framework 4.8 **没有任何托管 GCM 实现**（`AesManaged` 只支持 CBC/ECB/CFB）。

三条路：BouncyCastle（+3 MB，违背减体积初衷，否决）、纯 C# 手写 GHASH+CTR
（正确性与恒定时间风险高）、P/Invoke 系统 `bcrypt.dll`（零体积，系统实现）。
本项目走 `bcrypt.dll`。

**这里踩了整次迁移最难查的一个坑**：

```
BCryptEncrypt 返回 NTSTATUS 0xC000000D (STATUS_INVALID_PARAMETER)
```

最初用手工偏移 `Marshal.WriteInt32(infoBuf, offset, ...)` 拼
`BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO`，按 72 字节分配、`cbSize` 也填 72。
但它在 x64 上真实大小是 **88 字节**（中间有 3 处 4 字节对齐填充），
而内核要求 `cbSize` **精确等于** `sizeof(结构)`，否则直接拒绝 ——
报错信息完全不提「大小不对」，只说参数无效。

修法：用 `[StructLayout(LayoutKind.Sequential)]` 定义结构体，
`cbSize` 取 `Marshal.SizeOf<T>()`，让 CLR 算布局。
修好后 AES-128/192/256 往返、篡改检测、AAD、空载荷全部通过。

### 6.11 `SetLength` 不能另开 FileStream（会被自己的共享模式挡住）

下载引擎第一步就是 `SetLength(total)` 预分配。最初的实现是另开一个
`FileAccess.Write` 的 `FileStream` 去 `SetLength`，必然失败 ——
因为主句柄是以 `FileShare.Read` 打开的（只允许别人读），
这个新句柄会被它自己的共享模式拒绝：

```
IOException: 文件正由另一进程使用，该进程无法访问此文件
```

而 `SetFilePointerEx + SetEndOfFile` 也不行：在 `FILE_FLAG_OVERLAPPED`
句柄上 `SetFilePointerEx` 不生效（Win32 既定行为），`SetEndOfFile` 按
「当前指针」截断，组合起来会把文件截成 0 字节，**而且不报错**。

正解：`SetFileInformationByHandle + FileEndOfFileInfo`。
它直接给出末尾偏移，不依赖文件指针，所以对 OVERLAPPED 句柄有效。

### 6.12 `ERROR_IO_PENDING (997)` 是正常路径，不是失败

带 `FILE_FLAG_OVERLAPPED` 的句柄上，`WriteFile` 只负责把 IO 提交给内核：

| 返回值 | 含义 | 该做什么 |
|---|---|---|
| `true` | 同步完成（小文件常见） | 直接取结果 |
| `false` + `997` | **IO 已排队，正常** | 调 `GetOverlappedResult(bWait:true)` |
| `false` + 其它 | 真失败 | 抛异常 |

最初把 997 当错误抛出，报错文本是「重叠的 I/O 操作正在进行中」——
看着像并发冲突，实际是「等结果就行」。

### 6.13 同一 OVERLAPPED 句柄上并发提交 IO 会挂死

修好 6.12 之后单线程写正常，但 **8 线程并发写直接挂死**。
原因：`NativeOverlapped` 是 `stackalloc` 在各自线程栈上的（合法），
但句柄没有绑定 IO 完成端口，内核在有多个 pending 请求且 hEvent 为空时，
会等一个永远不会来的完成通知。

修法：用一把 `_ioGate` 锁把「提交 + 等结果」整段串行化。代价可接受 ——
每次写只有 16~96 KB，锁内是两次系统调用，没有用户态计算；
而且这把锁只串行化**同一文件的写入**，网络读取 / 校验 / UI 全部并行。
实测吞吐仍远超网络带宽（PoC 8.31 MB/s vs 真实需求 1.5 MB/s）。

修好后用专门探针验证了 15 项：8 线程并发写逐字节一致、
>4 GB 偏移（OffsetHigh）正确、EOF 返回 0、预分配与 OVERLAPPED 句柄共存。

### 6.14 `HttpClientHandler.UseCookies` 默认 true，会吞掉手工 Cookie 头

net48 的 `HttpClientHandler` 默认 `UseCookies = true`，此时它会**接管** Cookie 头，
通过 `request.Headers` 设的 Cookie 被静默忽略 ——
表现为「网盘直链的鉴权 Cookie 发不出去，服务端返回未登录」。

net8 的 `SocketsHttpHandler` 没有这个开关（默认不接管），所以只有 net48 要显式
`UseCookies = false`。

**这类「同一份代码在两框架下语义不同」的差异最危险**：不编译报错，只在运行时
悄悄丢功能。幸好 Smoke 里覆盖了「Cookie 透传到服务端」，一跑就暴露。

---

## 七、架构：兼容层是怎么组织的

核心原则：**业务代码零条件编译，差异全部收敛到 `Compat/` 一层。**

```
src/YunX.Core/Compat/
├── BclWrappers.cs            Math48 / Helpers / Hash48 / Rng48 / Pbkdf2_48 /
│                             Crypto48 / Net48Constants
│                             ← 两框架都编译，net8 分支转调 BCL
├── StringCompatExtensions.cs   Contains(x, comparison) / Split(char, opts) 等
├── StreamCompat.cs           Stream/File/Task 的异步重载 + Task.WaitAsync
├── ProcessAndHash.cs         Process48.KillTree / BuildArgumentString / HashCompat
├── PositionalFile.cs         pwrite 语义统一入口：net8→RandomAccess，net48→PwriteStream
├── PwriteStream.cs           net48 专用：WriteFile + NativeOverlapped
├── NativeInterop.cs          net48 专用：Win32 P/Invoke 声明
├── AesGcmCompat.cs           net8→BCL AesGcm，net48→bcrypt.dll P/Invoke
├── Sqlite48.cs               net48 专用：System.Data.SQLite 连接串构造
├── SqliteShim.cs             net48 专用：SqliteXxx 类型别名
├── IndexRangeShims.cs        net48 专用：System.Index / System.Range
├── LangShims.cs              net48 专用：IsExternalInit / RequiredMemberAttribute
└── GlobalUsings.cs           net48 缺的 global using + 无条件引入 YunX.Core.Compat
```

**「两框架都编译」是本方案能收敛住的关键**：`Math48.Clamp` 在 net8 上
直接 `return Math.Clamp(...)`（JIT 内联，零开销），net48 上手写边界判断。
业务代码只写 `Math48.Clamp(x, a, b)`，一个 `#if` 都不需要。

### 并发写这一层怎么统一的

`PositionalFile` 是唯一需要区分框架的热点路径。它用了方法体内 `#if`
而不是虚接口 —— 因为这是每 16KB 一次的热路径，虚分发是白花的开销：

```
net8 : File.OpenHandle → RandomAccess.WriteAsync / SetLength / FlushToDisk
net48: CreateFileW(FILE_FLAG_OVERLAPPED) → WriteFile(NativeOverlapped) + Task.Run
```

net48 侧的两个关键细节：

1. **`FILE_FLAG_OVERLAPPED` 必须设**。不设的话带 OVERLAPPED 的 `WriteFile`
   会**忽略 OVERLAPPED 里的偏移**，数据落到文件指针位置 ——
   所有分片互相覆盖，而且静默（写入成功、长度正确、内容全错）。
2. **`SetLength` 不能走 `SetFilePointerEx + SetEndOfFile`**。带 OVERLAPPED 的句柄上
   `SetFilePointerEx` 不移动文件指针（Win32 既定行为），而 `SetEndOfFile`
   以「当前指针」为准截断 —— 组合起来会把文件截成 0 字节且不报错。
   改用独立 `FileStream` 通道设长度。

---

## 八、结论

| 目标 | 现状 |
|---|---|
| 体积 1.6 MB 且零安装 | ✅ **已实现**（net48 目标） |
| 体积 70 MB 且软件环境无关 | ✅ **保留**（net8 目标） |
| 随时回退 | ✅ 双目标并存，各自独立可编译可发布 |

**当初判断「最大阻碍是 `RandomAccess`」方向对，但真正的工作量不在它。**
`RandomAccess` 只花了约 150 行封装（PoC 已证明性能无损）；
真正耗时的是一大片「.NET Core 新增的 BCL 便捷 API」——
`Math.Clamp` / `Convert.ToHexString` / `HashData` / `GeneratedRegex` /
`AesGcm` / `ProcessStartInfo.ArgumentList` / `HttpContent.ReadAsStringAsync(ct)` 等等，
散布在近 30 个文件里，零散且每一个都不难但不做就编译不过。

好在它们全部可以用「包装类 + 双分支」的机械方式解决，没有真正的技术障碍。
唯一需要人工判断的是**能力降级**（hosts 绕过 / HTTP/2），这已经在上文如实标注。
