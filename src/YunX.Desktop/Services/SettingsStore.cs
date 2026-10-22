using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using YunX.Core.Mirrors;
using YunX.Core.Net;

namespace YunX.Desktop.Services;

/// <summary>持久化到 <c>%APPDATA%\YunX-Desktop-CS\settings.json</c> 的设置。</summary>
public sealed class AppSettings
{
    public string DownloadDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public int Threads { get; set; } = 8;

    public bool Resume { get; set; } = true;

    /// <summary>限速（KB/s）；0 = 不限。</summary>
    public int SpeedLimitKb { get; set; }

    public bool PreemptSlowChunks { get; set; } = true;

    /// <summary>自动打开下载目录。</summary>
    public bool OpenFolderWhenDone { get; set; }

    /// <summary>联网模式：auto / direct / proxy。</summary>
    public string NetMode { get; set; } = "auto";

    /// <summary>用 DoH 绕过 hosts 劫持。</summary>
    public bool BypassHosts { get; set; } = true;

    /// <summary>GitHub Personal Access Token（可空）。</summary>
    public string GitHubToken { get; set; } = "";

    /// <summary>用户指定的线路 Id；"auto" = 自动择优。</summary>
    public string MirrorId { get; set; } = "auto";

    /// <summary>线路启用状态覆盖（Id → 是否启用）。仅在用户改过时存在。</summary>
    public Dictionary<string, bool>? MirrorEnabled { get; set; }

    /// <summary>自定义线路（用户添加的代理前缀）。</summary>
    public List<CustomMirrorDto>? CustomMirrors { get; set; }

    // ===== 本轮新增（对应原项目 FEATURES.md）=====

    /// <summary>同时下载的任务数上限（原项目默认 1：前台任务吃满带宽，其余排队）。</summary>
    public int MaxConcurrentTasks { get; set; } = 1;

    /// <summary>
    /// 已完成任务在列表里保留多少条历史。
    ///
    /// <para><b>为什么不设 0 关闭</b>：0 会让"重启后空列表"重现，用户无法区分
    /// 「没下过」和「记录丢了」。最小保留 1 条；想干净可以手动删任务。</para>
    ///
    /// <para>默认 100：足够看清最近下过什么，又不会让库无限增长。
    /// 每条记录约 300 字节（含 payload），100 条约 30 KB。</para>
    /// </summary>
    public int CompletedHistoryLimit { get; set; } = 100;

    /// <summary>侧边栏「收藏」区是否收起。记住用户的选择，避免每次启动都要再收一次。</summary>
    public bool FavoritesCollapsed { get; set; }

    /// <summary>分片临时文件目录。空 = 与下载目录相同（放 SSD 上通常更快）。</summary>
    public string? CacheDir { get; set; }

    /// <summary>分片级 SHA-256 校验（写入时边写边算，合并前比对）。</summary>
    public bool VerifyChunks { get; set; } = true;

    /// <summary>
    /// 完成后计算整个文件的 SHA-256。
    ///
    /// <para><b>默认关闭</b>：要完整读一遍文件，对大文件是实打实的额外 IO
    /// （50GB 就要多读 50GB）。分片级校验（<see cref="VerifyChunks"/>）已经在
    /// 写入时抽查过首尾，日常够用；这个开关是给"要拿哈希去比对"的场景用的。</para>
    /// </summary>
    public bool VerifyHash { get; set; }

    /// <summary>
    /// 下载完成时弹通知（托盘气泡）。
    ///
    /// <para>只在窗口不活跃时弹 —— 用户正盯着进度条看的时候再弹一个是噪音。
    /// 默认开启：批量下几十个文件时，用户会切去做别的事，完成时需要一个反馈。</para>
    /// </summary>
    public bool NotifyOnComplete { get; set; } = true;

    /// <summary>点击「下载」后是否自动切到下载页。</summary>
    public bool AutoSwitchToDownloads { get; set; } = true;

    /// <summary>关闭行为：ask / exit / tray。</summary>
    public string CloseBehavior { get; set; } = "ask";

    /// <summary>下载期间阻止系统睡眠。</summary>
    public bool KeepAwake { get; set; } = true;

    /// <summary>剪贴板分享链接检测。</summary>
    public bool ClipboardWatch { get; set; } = true;

    // ── 已移除：CheckUpdateOnStart / SkipUpdateVersion ──
    // 原「检查更新」去比对上游 Kotlin 项目 tidain/YunX-Desktop 的 Release tag，
    // 与本重构版（版本号从 1.0.0 重新起算）没有可比性：永远提示「发现新版本」，
    // 而那些包是 Kotlin 版，装上就是另一个程序。功能已整体移除。
    // 旧 config.json 里的这两个字段会被反序列化器忽略，不影响加载。

    /// <summary>诊断模式（6 模块分文件日志）。</summary>
    public bool DiagnosticsEnabled { get; set; }

    /// <summary>下载调试（按任务事件流 + 调试页）。</summary>
    public bool DownloadDebugEnabled { get; set; }

    /// <summary>凭证保护等级：portable / machine / user。</summary>
    public string KeyProtection { get; set; } = "machine";

    /// <summary>数据库加密等级描述（给 UI 显示）。</summary>
    public string KeyProtectionLabel => KeyProtection switch
    {
        "portable" => "宽松（密钥明文，可跨设备）",
        "user" => "较为严格（DPAPI，绑定当前 Windows 用户）",
        _ => "普通（绑定本机）",
    };

    /// <summary>主题种子色（HEX）。null = 默认蓝。</summary>
    public string? SeedColor { get; set; }

    /// <summary>实验性：读缓冲（KB）。</summary>
    public int ReadBufferKb { get; set; } = 96;

    /// <summary>实验性：抢占阈值（KB/s）。</summary>
    public int PreemptFloorKb { get; set; } = 12;

    /// <summary>实验性：抢占判定时长（秒）。</summary>
    public int PreemptAgeSec { get; set; } = 15;

    /// <summary>实验性：启用 HTTP/2。</summary>
    public bool EnableHttp2 { get; set; }

    /// <summary>按平台的线程数覆盖（平台 → 线程数）。</summary>
    public Dictionary<string, int>? ThreadsPerPlatform { get; set; }

    [JsonIgnore]
    public NetworkMode NetworkMode => NetMode switch
    {
        "direct" => NetworkMode.Direct,
        "proxy" => NetworkMode.Proxy,
        _ => NetworkMode.Auto,
    };
}

public sealed class CustomMirrorDto
{
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
}

/// <summary>设置的读写与原子落盘。</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder
            .UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;

    public SettingsStore() : this(null) { }

    /// <param name="keyProtection">
    /// 凭证保护等级（portable/machine/user）。为 null 时按默认 machine 建一个密钥 ——
    /// 密钥文件与 AppServices 用的是同一个（同一数据目录下的 credential.key），
    /// 所以两边能互解。
    ///
    /// <para><b>为什么在构造时就建 Cipher</b>：<c>Load()</c> 发生在 AppServices 之前，
    /// 若此时没有解密器，磁盘上的密文 Token 就只能被当成明文读出来 —— 表现为
    /// 「保存后重启，Token 变成 enc:v1:... 这串乱码」。必须让 Load 就能解密。</para>
    /// </param>
    public SettingsStore(string? keyProtection)
    {
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YunX-Desktop-CS");
        Directory.CreateDirectory(DataDirectory);
        _path = Path.Combine(DataDirectory, "settings.json");

        // ★ 只建对象，**不**在这里 Initialize。
        //
        //   Initialize → PBKDF2 210,000 轮派生主密钥，是纯 CPU 密集的同步操作
        //   （数百毫秒）。SettingsStore 在 MainViewModel 构造前创建，也就是在 UI 线程上 ——
        //   一写 new() 就等于让窗口出现前先卡一段。而绝大多数场景（首次启动、
        //   Token 为空）根本用不到加解密，这笔开销完全没必要付。
        //   改为首次真正加解密时才初始化（见 GetCipher）。
        _cipherLevel = keyProtection;
    }

    private readonly string? _cipherLevel;
    private YunX.Core.Security.CredentialCipher? _cipher;
    private bool _cipherTried;

    /// <summary>
    /// 取（必要时初始化）加密器。
    ///
    /// <para>首次调用会付出 PBKDF2 的开销；之后复用同一个实例。
    /// 密钥不可用（权限、损坏）时返回 null，调用方退化为明文 —— 绝不能让程序起不来。</para>
    /// </summary>
    private YunX.Core.Security.CredentialCipher? GetCipher()
    {
        if (_cipherTried) return _cipher;
        _cipherTried = true;

        try
        {
            var c = new YunX.Core.Security.CredentialCipher(DataDirectory,
                ParseLevel(_cipherLevel));
            c.Initialize();
            _cipher = c;
        }
        catch
        {
            _cipher = null;
        }
        return _cipher;
    }

    /// <summary>已就绪的加密器；未初始化时返回 null（供只读场景判断，不触发初始化）。</summary>
    public YunX.Core.Security.CredentialCipher? Cipher => _cipherTried ? _cipher : null;

    /// <summary>配置与密钥所在目录（与 AppServices.DataDirectory 相同）。</summary>
    public string DataDirectory { get; }

    private static YunX.Core.Security.KeyProtectionLevel ParseLevel(string? label) => label switch
    {
        "portable" => YunX.Core.Security.KeyProtectionLevel.Portable,
        "user" => YunX.Core.Security.KeyProtectionLevel.UserBound,
        _ => YunX.Core.Security.KeyProtectionLevel.MachineBound,
    };

    public string FilePath => _path;

    /// <summary>
    /// 敏感字段（目前是 GitHub Token）的加解密器。
    ///
    /// <para><b>为什么必须加密</b>：Token 是明文写进 settings.json 的，而这个文件
    /// 就在 <c>%APPDATA%</c> 下 —— 任何能读用户目录的程序/同步盘/备份都能拿到它。
    /// 网盘 Cookie 走的是 <see cref="CredentialCipher"/>（AES-GCM + DPAPI 保护主密钥），
    /// Token 却裸着，属于安全声明与实际不一致。</para>
    ///

    /// <summary>磁盘上 Token 的密文前缀，用于区分「已加密」与历史遗留的明文。</summary>
    private const string CipherPrefix = "enc:v1:";

    public AppSettings Load()
    {
        AppSettings settings;
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            var json = File.ReadAllText(_path);
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
        }
        catch
        {
            // 配置损坏不应导致启动失败
            return new AppSettings();
        }

        // 磁盘 → 内存：把密文解成明文。老配置里的明文原样保留（下次保存时自动升级为密文）。
        settings.GitHubToken = DecryptToken(settings.GitHubToken);
        return settings;
    }

    /// <summary>原子写入：临时文件 + 替换，掉电不会留下半截 JSON。</summary>
    public void Save(AppSettings settings)
    {
        try
        {
            // 内存对象是明文，写盘前加密到副本，避免把明文落到磁盘
            var toWrite = Clone(settings);
            toWrite.GitHubToken = EncryptToken(settings.GitHubToken);

            // ★ 临时文件名必须唯一。
            //   固定用 settings.json.tmp 时，两个实例（或保存与退出同时触发）会
            //   写同一个 tmp、各自 Move → 一边的改名会失败或把另一边的内容盖掉。
            string tmp = $"{_path}.{Guid.NewGuid():N}.tmp";

            string json = JsonSerializer.Serialize(toWrite, JsonOpts);
            JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);   // 自检：写出的必须是合法 JSON
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // 保存失败不抛出：用户改设置不该弹错误框
        }
    }

    private string EncryptToken(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var cipher = GetCipher();
        if (cipher == null) return plain;
        try { return CipherPrefix + cipher.Encrypt(plain); }
        catch { return plain; }     // 加密失败也不能丢用户输入
    }

    private string DecryptToken(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(CipherPrefix, StringComparison.Ordinal)) return stored;   // 历史明文
        var cipher = GetCipher();
        if (cipher == null) return "";   // 没有解密器：宁可让用户重填，也不假装有值
        try { return cipher.Decrypt(stored[CipherPrefix.Length..]) ?? ""; }
        catch { return ""; }
    }

    /// <summary>浅拷贝（字段都是值类型/字符串，够用）。</summary>
    private static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(s, JsonOpts), JsonOpts) ?? new AppSettings();

    /// <summary>按设置构造线路表（应用启用状态覆盖与自定义线路）。</summary>
    public static List<Mirror> BuildMirrors(AppSettings settings)
    {
        var list = MirrorRegistry.CreateDefault();

        if (settings.MirrorEnabled is { Count: > 0 } overrides)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (!overrides.TryGetValue(list[i].Id, out bool enabled)) continue;
                if (enabled == list[i].Enabled) continue;
                list[i] = new Mirror
                {
                    Id = list[i].Id, Name = list[i].Name, Kind = list[i].Kind,
                    Arg = list[i].Arg, Scopes = list[i].Scopes, Enabled = enabled,
                };
            }
        }

        if (settings.CustomMirrors is { Count: > 0 } customs)
        {
            int n = 0;
            foreach (var c in customs)
            {
                if (string.IsNullOrWhiteSpace(c.Prefix)) continue;
                list.Add(new Mirror
                {
                    Id = $"custom-{n++}", Name = c.Name.Length > 0 ? c.Name : c.Prefix,
                    Kind = MirrorKind.Prefix, Arg = c.Prefix,
                });
            }
        }

        return list;
    }
}
