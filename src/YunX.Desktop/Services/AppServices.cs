using System.IO;
using System.Net.Http;
using YunX.Core.Cloud;
using YunX.Core.Data;
using YunX.Core.Diagnostics;
using YunX.Core.Net;
using YunX.Core.Security;
using YunX.Core.Windows;
using YunX.GitHub;

namespace YunX.Desktop.Services;

/// <summary>
/// 应用服务容器（手写组合根）。
///
/// <para>没有引入 DI 框架：依赖只有十来个，且生命周期边界清晰（单例 / 会话级）。
/// 手写组合根让「谁依赖谁」一目了然，也避免第三方容器对单文件发布的额外体积。</para>
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly SettingsStore _store;

    /// <summary>网盘适配器共用的客户端，退出时释放。</summary>
    private readonly HttpClient _cloudClient;

    public AppSettings Settings { get; }
    public AppDatabase Database { get; }
    public CredentialCipher Cipher { get; }
    public CredentialRepository Credentials { get; }
    public TaskRepository Tasks { get; }
    public HistoryRepository History { get; }
    public FavoriteRepository Favorites { get; }
    public HttpClientProvider Http { get; }
    public DiagnosticLog Diagnostics { get; }
    public DownloadDebugLog DownloadDebug { get; }
    // 说明：曾在这里再建一个 SleepBlocker，但真正使用的是 MainWindow 里那个
    //（由它负责按「下载期间阻止睡眠」开关 Acquire/Release）。这里那个从未被 Acquire，
    //  留着只会让人误以为有两个保活源 —— 已删除。
    public CloudRegistry CloudDrives { get; }
    public GitHubApiClient GitHubApi { get; }
    public GitHubBrowser GitHubBrowser { get; }
    public SourceResolver Sources { get; }

    /// <summary>中文口令兜底解析器（星海）。只把口令换成 pwdId，不是网盘。</summary>
    public StarSeaCommandResolver StarSea { get; }

    /// <summary>数据根目录（数据库、密钥、诊断日志都在这里）。</summary>
    public string DataDirectory { get; }

    public AppServices(SettingsStore store, AppSettings settings)
    {
        _store = store;
        Settings = settings;

        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YunX-Desktop-CS");
        Directory.CreateDirectory(DataDirectory);

        // ---- 加密与数据库 ----
        Cipher = new CredentialCipher(DataDirectory, ParseLevel(settings.KeyProtection));
        Cipher.Initialize();

        // 注意：GitHub Token 的加解密由 SettingsStore 自己负责（它在构造时就建了
        // Cipher，共用同一个 credential.key）。这里不再回填，避免两处持有不同实例。

        Database = AppDatabase.OpenWithHealing(Path.Combine(DataDirectory, "yunx.db"));
        Credentials = new CredentialRepository(Database, Cipher);
        Tasks = new TaskRepository(Database);
        History = new HistoryRepository(Database);
        Favorites = new FavoriteRepository(Database);

        // ---- 网络 ----
        Http = new HttpClientProvider(settings.NetworkMode);

        // ---- 诊断 ----
        Diagnostics = new DiagnosticLog(DataDirectory) { Enabled = settings.DiagnosticsEnabled };
        DownloadDebug = new DownloadDebugLog { Enabled = settings.DownloadDebugEnabled };

        // ---- 网盘 ----
        var cloudClient = Http.CreateClient();
        _cloudClient = cloudClient;
        CloudDrives = CloudDriveFactory.Create(cloudClient, platform =>
        {
            var saved = Credentials.Load(platform);
            return saved?.Value;
        },
        // 蓝奏要手动跟随重定向（逐跳校验域名），但必须仍然走共享 handler 的
        // ConnectCallback，否则 hosts 绕过 / DoH 对它失效
        Http.CreateClientNoRedirect());

        // 中文口令兜底：星海只把口令换成 pwdId，再合成夸克/UC 链接，
        // 所以它不算网盘适配器，作为 SourceResolver 的一个可选依赖注入
        StarSea = new StarSeaCommandResolver(cloudClient);

        Sources = new SourceResolver(CloudDrives, StarSea)
        {
            Log = m => Diagnostics.Write(DiagModule.Operation, m),
        };

        // ---- GitHub ----
        GitHubApi = new GitHubApiClient(Http,
            () => string.IsNullOrWhiteSpace(settings.GitHubToken) ? null : settings.GitHubToken)
        {
            Log = m => Diagnostics.Write(DiagModule.Network, m),
        };
        GitHubApi.OnUnauthorized = () =>
        {
            settings.GitHubToken = "";
            _store.Save(settings);
        };
        GitHubBrowser = new GitHubBrowser(GitHubApi);
    }

    /// <summary>GitHub 解析委托（交给 <see cref="SourceResolver"/> 调用，避免 Core 依赖 GitHub 项目）。</summary>
    public async Task<Core.Cloud.SourceResolution?> ResolveGitHubAsync(string input,
        Core.Mirrors.MirrorPool pool, CancellationToken ct = default)
    {
        var resolver = new ContentResolver(GitHubApi, Http)
        {
            Log = m => Diagnostics.Write(DiagModule.Network, m),
        };
        var content = await resolver.ResolveAsync(input, pool, ct).ConfigureAwait(false);

        var result = new Core.Cloud.SourceResolution
        {
            Kind = Core.Cloud.SourceKind.GitHub,
            Platform = CloudPlatforms.GitHub,
            Title = content.Title,
            Note = content.Note,
        };
        foreach (var (url, name, size) in content.Items)
        {
            result.Items.Add(new Core.Cloud.ResolvedItem
            {
                Name = name,
                Locator = url,
                DirectUrl = url,
                Size = size,
            });
        }
        return result;
    }

    /// <summary>保存凭证（加密后入库）。</summary>
    public void SaveCredential(string platform, string value, string extra = "")
    {
        Credentials.Save(platform, value, extra);
        Diagnostics.Write(DiagModule.Crypto, $"保存凭证 {platform}（{LogRedactor.Secret(value)}）");
    }

    public string? GetCredential(string platform) => Credentials.Load(platform)?.Value;

    public void SaveSettings() => _store.Save(Settings);

    private static KeyProtectionLevel ParseLevel(string s) => s switch
    {
        "portable" => KeyProtectionLevel.Portable,
        "user" => KeyProtectionLevel.UserBound,
        _ => KeyProtectionLevel.MachineBound,
    };

    public void Dispose()
    {
        try { DownloadDebug.ClearAll(); } catch { /* ignore */ }

        // 顺序有讲究：日志最后关（上面的清理还会写日志），
        // 数据库在凭证之前关（凭证仓库依赖它），
        // 主密钥最后清零（中间任何一步都不应该再需要解密）。
        try { Diagnostics.Dispose(); } catch { /* ignore */ }
        try { Database.Dispose(); } catch { /* ignore */ }
        try { _cloudClient.Dispose(); } catch { /* ignore */ }
        try { Http.Dispose(); } catch { /* ignore */ }
        try { Cipher.Dispose(); } catch { /* ignore */ }
    }
}
