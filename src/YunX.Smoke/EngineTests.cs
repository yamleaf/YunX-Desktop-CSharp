using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using YunX.Core.Download;
using YunX.Core.Mirrors;
using YunX.Core.Net;
using YunX.Core.Util;

namespace YunX.Smoke;

/// <summary>
/// <see cref="DownloadEngine"/> 的确定性测试。
///
/// <para><b>为什么必须补这一层</b>：引擎是整个项目最复杂的模块（分片规划 / 断点续传 /
/// 慢连接抢占 / Range 降级 / 校验 全在这里），但在补这组测试之前它<b>零自动化覆盖</b>，
/// 只能靠 <c>-- live</c> 连真实网络人工看日志 —— 那种验证既不可重复，也覆盖不到
/// 「服务器忽略 Range」「直链过期」这类分支。</para>
///
/// <para><b>为什么不架 <c>HttpListener</c></b>：Windows 上它走 http.sys，非管理员
/// 进程需要预先注册 URL ACL（netsh http add urlacl），在 CI / 新机器上直接失败。
/// 这里改用裸 <see cref="TcpListener"/> 手写最小 HTTP/1.1 响应，无权限要求、无依赖。</para>
/// </summary>
/// <summary>断言回调（带默认 detail，便于「只看通过与否」的用例）。</summary>
internal delegate void CheckFn(string name, bool ok, string detail = "");

internal static class EngineTests
{
    public static void Run(CheckFn check, Action<string> section)
    {
        section("线路分类（网盘直链不进 GitHub 镜像）");
        TestUrlKind(check);

        section("下载引擎（本地 HTTP 服务）");
        RunAsync(check).GetAwaiter().GetResult();

        section("慢连接抢占 / 限速器（纯逻辑）");
        TestWatchdog(check);
        TestSpeedLimiter(check);

        section("断点元数据（并发写）");
        TestMetadataConcurrency(check);

        section("分片哨兵摘要 / 目标名预留");
        TestChunkProbe(check);
        TestEnsureUniqueRace(check);
    }

    private static void TestUrlKind(CheckFn check)
    {
        check("GitHub raw 仍归 Raw",
            UrlClassifier.Classify("https://raw.githubusercontent.com/o/r/m/a") == UrlKind.Raw);
        check("codeload 仍归 Archive",
            UrlClassifier.Classify("https://codeload.github.com/o/r/zip/HEAD") == UrlKind.Archive);
        check("release 资产仍归 Asset",
            UrlClassifier.Classify("https://github.com/o/r/releases/download/v1/a.zip")
                == UrlKind.Asset);

        // 这条是回归点：早期实现把它们都当 Asset，于是被 gh-proxy 之类前缀成
        // 「https://gh-proxy.com/https://cdn.quark.cn/...」，探测阶段白等好几个请求
        check("网盘直链归 Other（不被镜像前缀）",
            UrlClassifier.Classify("https://cdn.quark.cn/abc/def.bin") == UrlKind.Other);
        check("第三方站点归 Other",
            UrlClassifier.Classify("https://example.com/a.zip") == UrlKind.Other);
        check("本地回环归 Other",
            UrlClassifier.Classify("http://127.0.0.1:8080/f") == UrlKind.Other);

        // 默认线路池里，除直连外没有任何线路能吃 Other
        var owners = MirrorRegistry.CreateDefault()
            .Where(m => m.Kind != MirrorKind.Direct)
            .Count(m => m.Supports("https://cdn.quark.cn/abc/def.bin"));
        check("镜像线路不支持网盘直链", owners == 0, $"{owners} 条");
    }

    private static async Task RunAsync(CheckFn check)
    {
        // ---------- 1. 多线程分片 + 分片校验 ----------
        // 4MB / 4 线程 = 8 块 × 512KB（<256KB 的文件会被 ChunkPlanner 当成一块）
        await WithServerAsync(4 * 1024 * 1024, async (server, dir) =>
        {
            var payload = server.Payload;
            var result = await DownloadAsync(server.Url, dir, new DownloadOptions
            {
                Threads = 4,
                VerifyChunks = true,
                PreemptSlowChunks = false,
                StaggerConnect = false,
            }).ConfigureAwait(false);

            check("多线程下载完成（206 分片）", result.Mode == "multi",
                $"mode={result.Mode} chunks={result.ChunkCount}");
            check("分片数 > 1", result.ChunkCount > 1, $"{result.ChunkCount}");

            var onDisk = await FileCompat.ReadAllBytesAsync(result.FilePath).ConfigureAwait(false);
            check("内容与服务器完全一致（含分片校验通过）",
                Hash48.Sha256(onDisk).SequenceEqual(Hash48.Sha256(payload)));

            check("完成后不残留 .part", !File.Exists(result.FilePath + ".part"));
            check("服务器收到过 Range 请求", server.LastRequestHeaders.Contains("Range:", StringComparison.OrdinalIgnoreCase));
        }).ConfigureAwait(false);

        // ---------- 2. 自定义请求头透传（网盘直链必需） ----------
        await WithServerAsync(64 * 1024, async (server, dir) =>
        {
            var opts = new DownloadOptions
            {
                Threads = 1, StaggerConnect = false, PreemptSlowChunks = false,
            };
            opts.Headers["Referer"] = "https://pan.example.com/s/abc";
            opts.Headers["Cookie"] = "k=v; x=y";

            await DownloadAsync(server.Url, dir, opts).ConfigureAwait(false);

            check("Referer 透传到服务端",
                server.LastRequestHeaders.Contains("pan.example.com", StringComparison.Ordinal));
            check("Cookie 透传到服务端",
                server.LastRequestHeaders.Contains("k=v", StringComparison.Ordinal));
        }).ConfigureAwait(false);

        // ---------- 3. 探测后 Range 被忽略 → 整任务降级单流 ----------
        await WithServerAsync(256 * 1024, async (server, dir) =>
        {
            // 第 1 个请求是探测（要 206 才会走分片），之后突然返回 200 全量：
            // 这是 CDN 限流时真实存在的中间态
            server.IgnoreRangeFromRequest = 2;

            var result = await DownloadAsync(server.Url, dir, new DownloadOptions
            {
                Threads = 4,
                PreemptSlowChunks = false,
                StaggerConnect = false,
                RangeIgnoredTolerance = 2,
            }).ConfigureAwait(false);

            check("Range 被忽略后降级单流", result.Mode == "single", $"mode={result.Mode}");

            var onDisk = await FileCompat.ReadAllBytesAsync(result.FilePath).ConfigureAwait(false);
            check("降级后内容仍正确（不能把全量响应拼在分片后面）",
                Hash48.Sha256(onDisk).SequenceEqual(Hash48.Sha256(server.Payload)));
        }).ConfigureAwait(false);

        // ---------- 4. 探测通过但直链随后全部 403 → 必须有界失败 ----------
        await WithServerAsync(128 * 1024, async (server, dir) =>
        {
            server.FailAfterProbe = true;   // 探测成功，之后的分片请求全部 403

            var sw = Stopwatch.StartNew();
            bool failed = false;
            string msg = "";
            var logs = new ConcurrentQueue<string>();
            try
            {
                await DownloadAsync(server.Url, dir, new DownloadOptions
                {
                    Threads = 2,
                    PreemptSlowChunks = false,
                    StaggerConnect = false,
                }, timeoutSeconds: 30, log: m => logs.Enqueue(m)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is DownloadFailedException
                                        or RemoteProbe.ProbeFailedException
                                        or DownloadCancelledException)
            {
                failed = true;
                msg = $"{e.GetType().Name}: {e.Message}";
                foreach (var l in logs) Console.WriteLine("        · " + l);
            }
            sw.Stop();

            check("直链持续 403 时任务失败而不是永久卡住", failed,
                $"耗时 {sw.Elapsed.TotalSeconds:0.0}s · 请求 {server.Requests} 次 · {Truncate(msg)}");
            if (!failed)
                Console.WriteLine("        请求序列：" + string.Join(" | ", server.RequestLines));
            check("失败耗时有界（< 25s）", sw.Elapsed.TotalSeconds < 25,
                $"{sw.Elapsed.TotalSeconds:0.0}s");
        }).ConfigureAwait(false);

        // ---------- 4b. 探测阶段也必须携带鉴权头（网盘直链的 Referer / Cookie） ----------
        // 早期实现只在分片阶段 ApplyCustomHeaders，探测那一次请求是「裸」的：
        // 需要 Referer 硬校验的直链会在探测阶段全部 403，报「所有线路均不可用」，
        // 而实际上分片请求本可以成功。
        await WithServerAsync(1024 * 1024, async (server, dir) =>
        {
            var opts = new DownloadOptions
            {
                Threads = 2, PreemptSlowChunks = false, StaggerConnect = false,
            };
            opts.Headers["Referer"] = "https://pan.probe-test.example/s/xyz";
            opts.Headers["Cookie"] = "probe_cookie=1";

            await DownloadAsync(server.Url, dir, opts).ConfigureAwait(false);

            // 第一个请求就是探测（Range: bytes=0-0），它必须带上这两个头
            var first = server.RequestLog.FirstOrDefault();
            check("探测请求携带 Referer",
                first != null && first.Contains("pan.probe-test.example", StringComparison.Ordinal),
                first == null ? "无请求记录" : Truncate(first));
            check("探测请求携带 Cookie",
                first != null && first.Contains("probe_cookie=1", StringComparison.Ordinal));
        }).ConfigureAwait(false);

        // ---------- 4c. 429 限流要有退避重试，而不是把线路耗尽后报假失败 ----------
        // 429/503 是限流，不是资源问题。早期实现遇到非 206 一律「换线路」，
        // 于是每条线路都被依次打成 429，最后报「所有线路都试过了」——
        // 而实际上等几百毫秒重试就能成功。
        await WithServerAsync(1024 * 1024, async (server, dir) =>
        {
            server.RateLimitFirstRequests = 3;   // 前 3 个请求（含探测）回 429

            var result = await DownloadAsync(server.Url, dir, new DownloadOptions
            {
                Threads = 2, PreemptSlowChunks = false, StaggerConnect = false,
                ChunkRetries = 5,
            }, timeoutSeconds: 40).ConfigureAwait(false);

            var onDisk = await FileCompat.ReadAllBytesAsync(result.FilePath).ConfigureAwait(false);
            check("429 限流后最终下载成功",
                Hash48.Sha256(onDisk).SequenceEqual(Hash48.Sha256(server.Payload)),
                $"请求 {server.Requests} 次");
        }).ConfigureAwait(false);

        // ---------- 5. 取消语义（保留断点，不算失败） ----------
        await WithServerAsync(2 * 1024 * 1024, async (server, dir) =>
        {
            using var cts = new CancellationTokenSource();

            var opts = new DownloadOptions
            {
                Threads = 4,
                PreemptSlowChunks = false,
                StaggerConnect = false,
                VerifyChunks = false,
                // ★ 必须限速才测得出取消。
                //   本地回环上 2MB 只要 0.1 秒就下完：取消信号从进度回调（UI 线程）发出，
                //   引擎要到下一个读循环才看到，那时文件早已落盘改名 —— 表现成
                //   「点了取消但任务成功」，看着像 bug，其实是取消窗口太窄。
                //   限到 256KB/s 让整个过程约 8 秒，取消才能稳定落在传输中途。
                SpeedLimitBytesPerSec = 256 * 1024,
            };

            // 按**累计字节**触发取消（而不是「第 1 个分片完成」）：
            // 后者在 8 块里可能等第 1 块完成时全都快完了，时序不可控。
            long threshold = server.Payload.Length / 4;
            int fired = 0;

            bool cancelled = false;
            var cLogs = new ConcurrentQueue<string>();
            try
            {
                await DownloadAsync(server.Url, dir, opts, ct: cts.Token,
                    timeoutSeconds: 60, log: m => cLogs.Enqueue(m),
                    progress: new Progress<DownloadProgress>(p =>
                    {
                        if (p.BytesDone >= threshold
                            && Interlocked.CompareExchange(ref fired, 1, 0) == 0)
                            cts.Cancel();
                    })).ConfigureAwait(false);
            }
            catch (DownloadCancelledException) { cancelled = true; }
            catch (Exception e)
            {
                cLogs.Enqueue($"!! {e.GetType().Name}: {e.Message}");
            }

            check("取消被识别为「已取消」而非失败", cancelled);

            string partPath = Directory.GetFiles(dir, "*.part").FirstOrDefault() ?? "";
            check("取消后保留 .part", partPath.Length > 0);
            check("取消后保留断点元数据", partPath.Length > 0 && File.Exists(partPath + ".json"));
            check("取消后没有误删目标文件", !File.Exists(Path.Combine(dir, "file.bin")));
        }).ConfigureAwait(false);

        // ---------- 5a. 进度必须走到 100% 且阶段不再停在「下载中」 ----------
        // 真实故障：界面显示「26.03 MB / 26.06 MB　探测中」，进度条差一格。
        // 根因是 ProgressCounter.Add 有 120ms 节流 + 只在有字节流入时上报，
        // 收尾阶段（写完 → 合并 → 校验 → 改名）没有任何上报点，
        // 最后几十 KB 落在节流窗口里被直接丢掉。
        await WithServerAsync(3 * 1024 * 1024, async (server, dir) =>
        {
            var reports = new ConcurrentQueue<DownloadProgress>();
            var sync = SynchronizationContext.Current;

            // 用同步收集器而不是 Progress<T>：Progress 会异步投递到 UI 线程，
            // 测试里没有消息泵，回调可能还没执行完断言就跑完了。
            var collector = new CollectingProgress(reports);

            var result = await DownloadAsync(server.Url, dir, new DownloadOptions
            {
                Threads = 4, PreemptSlowChunks = false, StaggerConnect = false,
            }, progress: collector).ConfigureAwait(false);

            var list = reports.ToArray();
            long maxDone = list.Length == 0 ? 0 : list.Max(p => p.BytesDone);
            long finalTotal = list.Length == 0 ? 0 : list.Last().TotalBytes;

            check("进度回调有上报", list.Length > 0, $"{list.Length} 次");
            check("最终进度到达 100%（不留零头）",
                finalTotal > 0 && maxDone == finalTotal,
                $"{maxDone}/{finalTotal} 差 {finalTotal - maxDone} 字节");
            check("结束时不再报「下载中」（有收尾阶段上报）",
                list.Any(p => p.Phase is DownloadPhase.Merging
                              or DownloadPhase.Verifying
                              or DownloadPhase.Completed),
                string.Join(",", list.Select(p => p.Phase).Distinct()));
            check("完成时速度为 0（不残留旧速度）",
                list.Last().SpeedBytesPerSec == 0, $"{list.Last().SpeedBytesPerSec:0}");
            check("结果字节数与进度总量一致", result.Size == finalTotal,
                $"{result.Size} vs {finalTotal}");
        }).ConfigureAwait(false);

        // ---------- 5b. 完成后不残留任何中间文件（含缓存目录跨盘搬家） ----------
        // ① part 被 File.Move 改名成正式文件，本身已不存在；
        // ② part.json 元数据必须显式删；
        // ③ 缓存目录与下载目录不同时，缓存目录里也不能留东西（跨盘 Move 是「复制+删源」，
        //    失败会留半截成品；成功后源必须已删）。
        await WithServerAsync(512 * 1024, async (server, dir) =>
        {
            string cacheDir = Path.Combine(Path.GetTempPath(),
                "yunx-cache-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(cacheDir);
            try
            {
                string? reportedPart = null;
                var result = await DownloadAsync(server.Url, dir, new DownloadOptions
                {
                    Threads = 2, PreemptSlowChunks = false, StaggerConnect = false,
                    CacheDir = cacheDir,
                    OnPartPathResolved = p => reportedPart = p,
                }).ConfigureAwait(false);

                check("断点落点被上报给上层（供删除任务时回收）",
                    reportedPart != null && reportedPart.StartsWith(cacheDir, StringComparison.OrdinalIgnoreCase),
                    reportedPart ?? "未回调");

                check("完成后下载目录只剩正式文件",
                    Directory.GetFiles(dir).Length == 1 &&
                    File.Exists(Path.Combine(dir, "file.bin")),
                    string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));

                check("完成后缓存目录被清空（无 part / 无元数据 / 无 tmp）",
                    Directory.GetFileSystemEntries(cacheDir).Length == 0,
                    string.Join(", ", Directory.GetFileSystemEntries(cacheDir).Select(Path.GetFileName)));

                check("正式文件内容完整",
                    new FileInfo(result.FilePath).Length == server.Payload.Length);
            }
            finally
            {
                try { Directory.Delete(cacheDir, recursive: true); } catch { /* ignore */ }
            }
        }).ConfigureAwait(false);

        // ---------- 6. 断点续传（预置半个文件，确定性） ----------
        await WithServerAsync(2 * 1024 * 1024, async (server, dir) =>
        {
            long total = server.Payload.Length;
            long half = total / 2;

            // 造出「已经下了前一半」的现场：part 长度必须等于总长（引擎会校验），
            // 前半是真实数据，后半是占位 0（会被续传覆盖）
            var seeded = new byte[total];
            Array.Copy(server.Payload, seeded, half);
            string part = Path.Combine(dir, "file.bin.part");
            await FileCompat.WriteAllBytesAsync(part, seeded).ConfigureAwait(false);

            new PartMetadata
            {
                UrlKey = PartMetadata.UrlKeyOf(server.Url),
                Total = total,
                ETag = server.ETag.Trim('"'),
                ChunkSize = 512 * 1024,
                Mode = "multi",
                Done = new List<IntervalDto> { new() { Start = 0, End = half - 1 } },
            }.Save(part);

            int before = server.Requests;
            var r = await DownloadAsync(server.Url, dir, new DownloadOptions
            {
                Threads = 4, PreemptSlowChunks = false, StaggerConnect = false,
            }).ConfigureAwait(false);

            check("复用既有断点（Resumed=true）", r.Resumed, $"resumed={r.Resumed}");

            var onDisk = await FileCompat.ReadAllBytesAsync(r.FilePath).ConfigureAwait(false);
            check("续传后内容与服务器一致（断点没拼错位）",
                Hash48.Sha256(onDisk).SequenceEqual(Hash48.Sha256(server.Payload)),
                $"{onDisk.Length}/{total} 字节");

            // 只该请求后半段：若从头重下，续传就白做了
            var ranges = server.RequestLines.Skip(before).ToList();
            check("续传只请求剩余区间",
                ranges.Any(l => l.Contains($"bytes={half}-", StringComparison.Ordinal)),
                string.Join(" | ", ranges));
        }).ConfigureAwait(false);

        // ---------- 6b. 单流续传：半截 part 必须能接着下 ----------
        // 单流路径早期不写元数据、复用条件又要求「长度 == 总长」，
        // 未完成的 part 长度必然 < total → 永远从头重下。
        // GitHub 仓库 zip（codeload 不支持 Range，必然单流）正是最常用场景。
        await WithServerAsync(1024 * 1024, async (server, dir) =>
        {
            long total = server.Payload.Length;
            long half = total / 2;

            // 模拟单流被中断：没有预分配，长度就是已下载量
            string part = Path.Combine(dir, "file.bin.part");
            await FileCompat.WriteAllBytesAsync(part, server.Payload.AsSpan(0, (int)half).ToArray())
                .ConfigureAwait(false);

            new PartMetadata
            {
                UrlKey = PartMetadata.UrlKeyOf(server.Url),
                Total = total,
                ETag = server.ETag.Trim('"'),
                ChunkSize = 0,
                Mode = "single",
                Done = new List<IntervalDto> { new() { Start = 0, End = half - 1 } },
            }.Save(part);

            // Range 全忽略 = 强制单流
            server.IgnoreRangeFromRequest = 0;

            int before = server.Requests;
            var r = await DownloadAsync(server.Url, dir, new DownloadOptions
            {
                Threads = 4, PreemptSlowChunks = false, StaggerConnect = false,
            }).ConfigureAwait(false);

            check("单流：半截 part 能复用续传", r.Resumed, $"resumed={r.Resumed} mode={r.Mode}");

            var onDisk = await FileCompat.ReadAllBytesAsync(r.FilePath).ConfigureAwait(false);
            check("单流续传后内容正确",
                Hash48.Sha256(onDisk).SequenceEqual(Hash48.Sha256(server.Payload)),
                $"{onDisk.Length}/{total}");

            // 不该再有一份被改名的孤儿 part（旧逻辑会 EnsureUnique 成 "file (1).bin.part"）
            check("单流续传不留孤儿 .part",
                Directory.GetFiles(dir, "*.part").Length == 0,
                string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));
        }).ConfigureAwait(false);
    }
    // =====================================================================
    //  纯逻辑测试
    // =====================================================================

    private static void TestWatchdog(CheckFn check)
    {
        var tuning = new DownloadTuning { PreemptMinAgeMs = 0, PreemptFloorBps = 64 * 1024 };
        var wd = new SlowChunkWatchdog { Enabled = true };

        // 5 个在飞分片：4 快 1 慢。5 > EndgameInflight(3)，走「常规判定」分支。
        // 快：3.2 秒写了 4MB ≈ 1.25MB/s；慢：3.2 秒只写 4KB ≈ 1.2KB/s
        var chunkSize = 8L * 1024 * 1024;
        var states = new List<ChunkState>();
        for (int i = 0; i < 4; i++)
        {
            var fast = new ChunkState(i * chunkSize, (i + 1) * chunkSize - 1, workerId: i);
            fast.AddBytes(4 * 1024 * 1024);
            states.Add(fast);
        }
        var slow = new ChunkState(4 * chunkSize, 5 * chunkSize - 1, workerId: 4);
        slow.AddBytes(4096);
        states.Add(slow);

        Thread.Sleep(3200);
        long downloaded = 4L * 4 * 1024 * 1024 + 4096;

        // 抢占判据要求「连续两次采样都偏慢」，所以先空跑一轮 sweep 建立基线，
        // 再等一个采样窗口后做正式判定。第一轮不应抢任何东西。
        int warmup = wd.Sweep(states, downloaded, 3200, workers: 5, null, tuning);
        check("首次采样不足以判定（不急于抢占）", warmup == 0, $"taken={warmup}");
        Thread.Sleep(1500);

        int taken = wd.Sweep(states, downloaded, 4700, workers: 5, null, tuning);

        check("只抢占慢分片，快分片不动",
            taken == 1 && slow.PreemptRequested &&
            states.Take(4).All(x => !x.PreemptRequested),
            $"taken={taken} slowBps={Humanize.Speed(slow.LastBps)}");

        // 刚抢占过 → 冷却期内不能再抢（否则会重连风暴）
        int again = wd.Sweep(states, downloaded, 4700, workers: 5, null, tuning);
        check("冷却期内不重复抢占", again == 0, $"taken={again}");

        foreach (var s in states) s.Dispose();

        // 收尾分支：在飞只剩 1 个时门槛放宽到 EndgameMinAgeMs(3s)
        var tail = new ChunkState(0, chunkSize - 1, workerId: 0);
        tail.AddBytes(2048);
        Thread.Sleep(3200);
        int tailWarm = wd.Sweep(new[] { tail }, 2048, 3200, workers: 1, null, tuning);
        Thread.Sleep(1500);
        int tailTaken = wd.Sweep(new[] { tail }, 2048, 4700, workers: 1, null, tuning);
        check("收尾阶段（在飞 ≤ 3）也抢占慢连接",
            tailWarm == 0 && tailTaken == 1 && tail.PreemptRequested,
            $"warm={tailWarm} taken={tailTaken}");
        tail.Dispose();

        // 关闭开关后什么都不做
        var off = new SlowChunkWatchdog { Enabled = false };
        var s2 = new ChunkState(0, chunkSize - 1, 0);
        s2.AddBytes(1);
        Thread.Sleep(600);
        check("关掉开关即不抢占", off.Sweep(new[] { s2 }, 1, 600, 1, null, tuning) == 0);
        s2.Dispose();

        // ★ 回归：所有连接速度**均匀且都不慢**时，一个都不能抢。
        //   旧公式用「任务总速 / workers」当参照，8 路各 95KB/s 时阈值会算成
        //   110KB/s —— 每路都低于阈值，于是全部被判成慢连接反复重连，
        //   越抢越慢（日志里可见「95.25 KB/s < 阈值 110.27 KB/s」）。
        //   用户设的绝对下限压到 1KB/s，确保判定只依赖自适应部分。
        var evenTuning = new DownloadTuning { PreemptMinAgeMs = 0, PreemptFloorBps = 1024 };
        var even = new List<ChunkState>();
        const long evenChunk = 4L * 1024 * 1024;
        const int rate95 = 95 * 1024;                 // 每路 95KB/s

        for (int i = 0; i < 8; i++)
        {
            var c = new ChunkState(i * evenChunk, (i + 1) * evenChunk - 1, i);
            c.AddBytes(rate95 * 3);
            even.Add(c);
        }
        Thread.Sleep(3200);

        // 第一轮建立基线
        wd.Sweep(even, 8L * rate95 * 3, 3200, workers: 8, null, evenTuning);

        // 保持同样的速度再补一次采样（不加字节的话第二次采样会算出 0 = 假慢）
        foreach (var c in even) c.AddBytes(rate95 * 2);
        Thread.Sleep(1600);

        int evenTaken = wd.Sweep(even, 8L * rate95 * 3, 4800, workers: 8, null, evenTuning);
        check("速度均匀时不误判慢连接（不反复重连）", evenTaken == 0,
            $"taken={evenTaken} 各自 {Humanize.Speed(even[0].LastBps)}");
        foreach (var c in even) c.Dispose();

        // 真正的离群值仍然要被抓住：7 路正常 + 1 路只有 1/20 速度
        // 用新的看门狗实例：上一轮已推进过采样窗口，复用同一实例会让判定依赖时序
        var wd2 = new SlowChunkWatchdog { Enabled = true };
        var mixed = new List<ChunkState>();
        for (int i = 0; i < 8; i++)
        {
            var c = new ChunkState(i * evenChunk, (i + 1) * evenChunk - 1, i);
            c.AddBytes(i == 7 ? rate95 * 3 / 20 : rate95 * 3);
            mixed.Add(c);
        }
        Thread.Sleep(3200);
        wd2.Sweep(mixed, 8L * rate95 * 3, 3200, workers: 8, null, evenTuning);

        for (int i = 0; i < 8; i++)
        {
            mixed[i].AddBytes(i == 7 ? rate95 * 2 / 20 : rate95 * 2);
        }
        Thread.Sleep(1600);

        int mixedTaken = wd2.Sweep(mixed, 8L * rate95 * 3, 4800, workers: 8, null, evenTuning);
        check("其中一路明显掉队时只抢它",
            mixedTaken == 1 && mixed[7].PreemptRequested && !mixed[0].PreemptRequested,
            $"taken={mixedTaken} 慢的={Humanize.Speed(mixed[7].LastBps)}");
        foreach (var c in mixed) c.Dispose();
    }

    private static void TestSpeedLimiter(CheckFn check)
    {
        var unlimited = new SpeedLimiter(0);
        var sw = Stopwatch.StartNew();
        unlimited.AcquireAsync(8 * 1024 * 1024).GetAwaiter().GetResult();
        sw.Stop();
        check("限速=0 时不等待", sw.ElapsedMilliseconds < 50, $"{sw.ElapsedMilliseconds}ms");

        // 100KB/s，桶初始满：先花掉桶里的量，再要一大块就会真的等
        var limiter = new SpeedLimiter(100 * 1024);
        limiter.AcquireAsync(90 * 1024).GetAwaiter().GetResult();

        sw.Restart();
        limiter.AcquireAsync(100 * 1024).GetAwaiter().GetResult();
        sw.Stop();

        // 需要补 90KB → 约 0.9 秒；给足容差，只验证「确实在限速」
        check("令牌不足时按速率等待", sw.ElapsedMilliseconds >= 500, $"{sw.ElapsedMilliseconds}ms");
        check("等待时间不超过合理上限（< 3s）", sw.ElapsedMilliseconds < 3000,
            $"{sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// 哨兵摘要必须「写入侧采到的字节」与「校验侧要读的区间」严格一致。
    /// 这段是 VerifyChunks 的正确性根基：两者差一个字节，每次校验都会失败，
    /// 下载变成永远重试。
    /// </summary>
    private static void TestChunkProbe(CheckFn check)
    {
        // ---- 大分片：首尾各 16KB，中间不采 ----
        int size = 200_000;
        var data = new byte[size];
        new Random(7).NextBytes(data);

        var st = new ChunkState(0, size - 1, 0);
        st.BeginHash();

        // 故意用不等长的块喂（模拟 64KB 读缓冲在尾部不足一块）
        int[] blocks = { 65_536, 65_536, 68_928 };
        long pos = 0;
        foreach (var b in blocks)
        {
            st.HashBytes(pos, data.AsSpan((int)pos, b));
            pos += b;
        }

        var digest = st.FinishHash();
        check("哨兵摘要已产出", digest != null);
        if (digest == null) { st.Dispose(); return; }

        var (headHex, tailHex, headLen, tailLen) = digest.Value;
        string expectHead = Helpers.ToHexString(Hash48.Sha256(data.AsSpan(0, headLen))).ToLowerInvariant();
        string expectTail = Helpers.ToHexString(
            Hash48.Sha256(data.AsSpan(size - tailLen, tailLen))).ToLowerInvariant();

        check("头部哨兵 = 前 16KB", headHex == expectHead, $"headLen={headLen}");
        check("尾部哨兵 = 后 16KB", tailHex == expectTail, $"tailLen={tailLen}");
        check("哨兵长度不超过 16KB", headLen <= ChunkState.ProbeBytes && tailLen <= ChunkState.ProbeBytes,
            $"{headLen}/{tailLen}");
        st.Dispose();

        // ---- 小分片：首尾各占一半，全长都被覆盖 ----
        var small = new byte[1000];
        new Random(9).NextBytes(small);
        var st2 = new ChunkState(0, small.Length - 1, 0);
        st2.BeginHash();
        st2.HashBytes(0, small);
        var d2 = st2.FinishHash();
        check("小分片首尾各占一半",
            d2 != null && d2.Value.HeadLen == 500 && d2.Value.TailLen == 500,
            d2 == null ? "null" : $"{d2.Value.HeadLen}/{d2.Value.TailLen}");
        st2.Dispose();

        // ---- 错位必须被检出（这是校验存在的理由）----
        // 模拟 pwrite 偏移算错：数据整体平移 1 字节落到文件里
        var shifted = new byte[size];
        Array.Copy(data, 0, shifted, 1, size - 1);

        var st3 = new ChunkState(0, size - 1, 0);
        st3.BeginHash();
        st3.HashBytes(0, data);                      // 写入侧按「正确位置」算摘要
        var d3 = st3.FinishHash()!.Value;
        string actualHead = Helpers.ToHexString(
            Hash48.Sha256(shifted.AsSpan(0, d3.HeadLen))).ToLowerInvariant();
        check("数据错位会被哨兵检出", actualHead != d3.Head);
        st3.Dispose();
    }

    /// <summary>
    /// 同名文件并发时不能拿到同一条目标路径 —— 否则两个任务写同一个 .part，
    /// 数据交错、落出损坏文件，而两边都以为成功。
    /// （GitHub 批量下载最典型：每个子目录都有 README.md）
    /// </summary>
    private static void TestEnsureUniqueRace(CheckFn check)
    {
        string dir = NewTempDir();
        try
        {
            var got = new ConcurrentBag<string>();
            Parallel.For(0, 64, _ => got.Add(FileNamePolicy.EnsureUnique(dir, "README.md")));

            var distinct = got.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            check("64 个并发请求拿到 64 个不同路径", distinct == 64, $"{distinct}/64");

            string first = FileNamePolicy.EnsureUnique(dir, "unique.bin");
            check("首次请求用原名", Path.GetFileName(first) == "unique.bin", Path.GetFileName(first));

            // 释放后同一个名字可以再次被分配（失败重试路径依赖这个）
            FileNamePolicy.Release(first);
            string again = FileNamePolicy.EnsureUnique(dir, "unique.bin");
            check("释放后同名可复用", Path.GetFileName(again) == "unique.bin", Path.GetFileName(again));
            FileNamePolicy.Release(again);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    private static void TestMetadataConcurrency(CheckFn check)
    {
        string dir = NewTempDir();
        string part = Path.Combine(dir, "sample.bin.part");

        // 8 个线程并发写同一个 part 的元数据 40 次 —— 旧实现（固定 .tmp 名 + 无锁）
        // 会在这里落下半截 JSON，导致下一次 TryLoad 返回 null（静默从 0 重下）
        var errors = new ConcurrentBag<Exception>();
        Parallel.For(0, 8, i =>
        {
            for (int n = 0; n < 40; n++)
            {
                try
                {
                    new PartMetadata
                    {
                        UrlKey = "127.0.0.1/file.bin",
                        Total = 1024 * 1024,
                        ChunkSize = 64 * 1024,
                        Done = new List<IntervalDto>
                        {
                            new() { Start = i * 1000L, End = i * 1000L + n },
                        },
                    }.Save(part);
                }
                catch (Exception e) { errors.Add(e); }
            }
        });

        check("并发写元数据不抛异常", errors.IsEmpty, errors.Count == 0 ? "" : errors.First().Message);

        var loaded = PartMetadata.TryLoad(part);
        check("并发写后元数据仍可解析（不是半截 JSON）", loaded != null);
        check("元数据内容自洽", loaded is { Total: 1024 * 1024, Done.Count: 1 });

        // 写失败 / 强杀可能留下 *.tmp，Delete 应该能一并清掉
        string stale = part + ".json.deadbeef.tmp";
        File.WriteAllText(stale, "{ broken");
        PartMetadata.Delete(part);
        check("Delete 清掉残留临时文件", !File.Exists(stale) && !File.Exists(part + ".json"));

        TryDeleteDir(dir);
    }

    // =====================================================================
    //  辅助
    // =====================================================================

    private static async Task<DownloadResult> DownloadAsync(string url, string dir,
        DownloadOptions options, CancellationToken ct = default, int timeoutSeconds = 60,
        Action<string>? log = null, IProgress<DownloadProgress>? progress = null)
    {
        var provider = new HttpClientProvider(NetworkMode.Direct);
        try
        {
            // 空线路池 = 只有直连，测试绝不碰外网
            var pool = new MirrorPool(Array.Empty<Mirror>(),
                provider.CreateClient(forDownload: true), provider);
            var engine = new DownloadEngine(provider, pool);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            return await engine.DownloadAsync(url, dir, "file.bin", options, progress, log, cts.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            provider.Dispose();
        }
    }

    private static async Task WithServerAsync(int payloadSize,
        Func<LocalFileServer, string, Task> body)
    {
        using var server = new LocalFileServer(payloadSize);
        string dir = NewTempDir();
        try
        {
            await body(server, dir).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "yunx-engine-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 文件仍被占用时忽略 */ }
    }

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "…";
}

/// <summary>
/// 同步收集进度上报。
///
/// <para><b>为什么不用 <see cref="Progress{T}"/></b>：它会把回调 Post 到创建时的
/// <see cref="SynchronizationContext"/>；测试是控制台程序、没有消息泵，
/// 回调可能一直排在队列里，断言时看到的是空集合 —— 那会得出「引擎没上报」的错误结论。</para>
/// </summary>
internal sealed class CollectingProgress : IProgress<DownloadProgress>
{
    private readonly ConcurrentQueue<DownloadProgress> _sink;
    public CollectingProgress(ConcurrentQueue<DownloadProgress> sink) => _sink = sink;
    public void Report(DownloadProgress value) => _sink.Enqueue(value);
}

/// <summary>
/// 最小 HTTP/1.1 服务器：只实现下载引擎需要的部分（Range / ETag / 状态码注入）。
///
/// <para>每个响应都带 <c>Connection: close</c> 并关闭 socket —— 客户端因此不会复用连接，
/// 每个请求的行为完全由下面的开关决定，测试结果是确定的。</para>
/// </summary>
internal sealed class LocalFileServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;

    public byte[] Payload { get; }
    public string ETag { get; set; } = "\"v1\"";

    /// <summary>从第几个请求开始忽略 Range（1 = 一开始就忽略）。</summary>
    public int IgnoreRangeFromRequest { get; set; } = int.MaxValue;

    /// <summary>
    /// true = 只放行探测请求（<c>Range: bytes=0-0</c>），其余请求一律返回
    /// <see cref="ForcedStatus"/>。这就是「探测成功、直链随即过期」的真实故障形态。
    ///
    /// <para>不用「第 N 个请求开始失败」是因为 HttpClient 在连接被复位时可能自动重试，
    /// 请求序号会漂移，测试就不确定了。</para>
    /// </summary>
    public bool FailAfterProbe { get; set; }

    public int ForcedStatus { get; set; } = 403;

    /// <summary>
    /// 前 N 个请求一律回 429（带 Retry-After），之后的请求正常处理。
    /// 用来验证引擎的限流退避：没有退避时，每条线路会被依次打成 429，
    /// 最后报「所有线路都试过了」的假失败。
    /// </summary>
    public int RateLimitFirstRequests { get; set; }

    /// <summary>每个请求的完整头（按到达顺序），用于断言「探测也带了鉴权头」。</summary>
    public ConcurrentQueue<string> RequestLog { get; } = new();

    public int Requests => Volatile.Read(ref _requests);
    private int _requests;

    public string LastRequestHeaders { get; private set; } = "";

    /// <summary>收到的请求行（诊断用）。</summary>
    public ConcurrentQueue<string> RequestLines { get; } = new();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string Url => $"http://127.0.0.1:{Port}/file.bin";

    public LocalFileServer(int payloadSize)
    {
        Payload = new byte[payloadSize];
        new Random(20261021).NextBytes(Payload);      // 固定种子：失败可复现

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _accept = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            #if NET48
            // net48 的 TcpListener.AcceptTcpClientAsync 没有 ct 重载。
            // 取消时 Stop() 会让它抛异常，catch 里 return 即可 —— 语义等价。
            try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
#else
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
#endif
            catch { return; }

            _ = Task.Run(() => HandleAsync(client), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var ns = client.GetStream();
                using var reader = new StreamReader(ns, Encoding.ASCII, false, 4096, leaveOpen: true);

                string? requestLine = await ReadLineCompatAsync(reader).ConfigureAwait(false);
                if (string.IsNullOrEmpty(requestLine)) return;

                int index = Interlocked.Increment(ref _requests);

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    string? line = await ReadLineCompatAsync(reader).ConfigureAwait(false);
                    if (line == null || line.Length == 0) break;
                    int colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                LastRequestHeaders = string.Join("\n", headers.Select(kv => $"{kv.Key}: {kv.Value}"));
                RequestLog.Enqueue(requestLine + "\n" + LastRequestHeaders);

                // 记录请求行 + Range，便于断言「续传只请求了剩余区间」
                headers.TryGetValue("Range", out var rangeForLog);
                RequestLines.Enqueue($"{index}: {requestLine} [Range:{rangeForLog ?? "-"}]");

                await WriteResponseAsync(ns, headers, index).ConfigureAwait(false);
            }
            catch
            {
                // 客户端提前断开（抢占 / 取消）属正常
            }
        }
    }

    private async Task WriteResponseAsync(NetworkStream ns, Dictionary<string, string> headers,
        int index)
    {
        int status = 200;
        string reason = "OK";
        long start = 0;
        long end = Payload.Length - 1;
        string? contentRange = null;

        headers.TryGetValue("Range", out var rangeHeader);

        bool isProbe = rangeHeader != null &&
                       rangeHeader.Replace(" ", "") == "bytes=0-0";

        if (FailAfterProbe && !isProbe)
        {
            status = ForcedStatus;
            reason = ReasonOf(ForcedStatus);
            end = -1;
        }
        else if (index <= RateLimitFirstRequests)
        {
            // 限流：任何请求（包括探测）都回 429，并要求 200ms 后再试
            status = 429;
            reason = "Too Many Requests";
            end = -1;
        }
        else if (index < 0)   // 保留形态：显式不可达
        {
            status = 400;
            reason = "Bad Request";
            end = -1;
        }
        else if (rangeHeader != null && index < IgnoreRangeFromRequest)
        {
            // 断点续传的 If-Range：内容变了就必须回 200 全量，不能回 206
            bool etagMatches = !headers.TryGetValue("If-Range", out var ifRange)
                               || ifRange.Trim('"') == ETag.Trim('"');

            if (etagMatches && TryParseRange(rangeHeader, Payload.Length, out start, out end))
            {
                status = 206;
                reason = "Partial Content";
                contentRange = $"bytes {start}-{end}/{Payload.Length}";
            }
            else
            {
                status = 416;
                reason = "Range Not Satisfiable";
                end = -1;
                contentRange = $"bytes */{Payload.Length}";
            }
        }

        long length = status is 200 or 206 ? end - start + 1 : 0;

        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status} {reason}\r\n");
        sb.Append("Accept-Ranges: bytes\r\n");
        sb.Append($"ETag: {ETag}\r\n");
        sb.Append("Content-Type: application/octet-stream\r\n");
        sb.Append("Connection: close\r\n");
        if (contentRange != null) sb.Append($"Content-Range: {contentRange}\r\n");
        if (status == 429) sb.Append("Retry-After: 0\r\n");   // 0 秒 → 引擎按指数退避起步
        sb.Append($"Content-Length: {length}\r\n\r\n");

        byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
        await ns.WriteAsync(head, _cts.Token).ConfigureAwait(false);

        if (length > 0)
            await ns.WriteAsync(Payload.AsMemory((int)start, (int)length), _cts.Token)
                .ConfigureAwait(false);

        await ns.FlushAsync(_cts.Token).ConfigureAwait(false);
    }

    private static string ReasonOf(int status) => status switch
    {
        403 => "Forbidden",
        404 => "Not Found",
        416 => "Range Not Satisfiable",
        429 => "Too Many Requests",
        _ => "Error",
    };

    /// <summary>解析 <c>bytes=a-b</c> / <c>bytes=a-</c>；越界返回 false（调用方回 416）。</summary>
    private static bool TryParseRange(string header, long total, out long start, out long end)
    {
        start = 0;
        end = -1;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;

        string spec = header[6..].Trim();
        int dash = spec.IndexOf('-');
        if (dash <= 0) return false;

        if (!long.TryParse(spec[..dash], out long a) || a < 0) return false;
        if (a >= total) return false;                     // 起点越界 → 416

        long b = total - 1;
        string tail = spec[(dash + 1)..].Trim();
        if (tail.Length > 0 && long.TryParse(tail, out long parsed)) b = Math.Min(parsed, total - 1);
        if (b < a) return false;

        start = a;
        end = b;
        return true;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _accept.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
        _cts.Dispose();
    }

    /// <summary>
    /// <c>StreamReader.ReadLineAsync(ct)</c> 的跨框架包装。
    /// net48 只有无参版 —— 测试服务端读请求行/头是短操作，取消由 Stop() 兜底。
    /// </summary>
    private static
#if NET48
        Task<string?>
#else
        ValueTask<string?>
#endif
        ReadLineCompatAsync(StreamReader reader)
    {
#if NET48
        return reader.ReadLineAsync();
#else
        return reader.ReadLineAsync(CancellationToken.None);
#endif
    }
}
