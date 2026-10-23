using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using YunX.Core.Cloud;
using YunX.Core.Data;
using YunX.Core.Diagnostics;
using YunX.Core.Download;
using YunX.Core.Mirrors;
using YunX.Core.Net;
using YunX.Core.Util;
using YunX.Desktop.Mvvm;
using YunX.Desktop.Services;
using YunX.GitHub;

namespace YunX.Desktop.ViewModels;

/// <summary>网盘账号页的一行。</summary>
public sealed class CloudAccountRow : Mvvm.ObservableObject
{
    private bool _loggedIn;
    private string _status = "未登录";

    public required string Platform { get; init; }
    public required string DisplayName { get; init; }
    public Core.Cloud.LoginDescriptor? Login { get; init; }

    public bool IsLoggedIn { get => _loggedIn; set => SetProperty(ref _loggedIn, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public string LoginHint => Login?.Hint ?? "无需登录";
    public bool NeedsCookie => Login?.CookieDomains.Length > 0;
}

/// <summary>主界面分区。</summary>
public enum AppPage
{
    Parse = 0,
    Downloads = 1,
    Accounts = 2,
    Debug = 3,
    Settings = 4,

    /// <summary>收藏夹。</summary>
    Favorites = 5,
}

/// <summary>Release 资产的可勾选行。</summary>
public sealed class AssetItemViewModel : ObservableObject
{
    private bool _selected = true;
    public required string Name { get; init; }
    public required string Url { get; init; }
    public long Size { get; init; }
    public string SizeText => Size > 0 ? Humanize.Size(Size) : "—";

    /// <summary>网盘目录项。选它没有意义，只能点进去。</summary>
    public bool IsDirectory { get; init; }

    /// <summary>网盘文件 ID / 平台令牌（取直链要用）。</summary>
    public string Locator { get; init; } = "";
    public string Token { get; init; } = "";

    /// <summary>可选（文件）——目录项不显示勾选框。</summary>
    public bool IsFile => !IsDirectory;

    public bool IsSelected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }
}

/// <summary>主界面 ViewModel。</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// 配置存储。
    ///
    /// <para><b>不能在字段初始化器里 <c>new()</c></b>：它的构造会
    /// <c>CredentialCipher.Initialize()</c> → PBKDF2 **210,000 轮**派生主密钥，
    /// 这是纯 CPU 密集的同步操作（数百毫秒）。字段初始化器在 ViewModel 构造前执行，
    /// 等于在 UI 线程上白白卡一段 —— 窗口还没出来就先僵住了。</para>
    ///
    /// <para>改成懒创建：构造时只建立一个空壳，真正需要读配置时才付出那笔开销。</para>
    /// </summary>
    private SettingsStore? _storeLazy;
    private SettingsStore _store => _storeLazy ??= new SettingsStore();
    private readonly SynchronizationContext _ui;

    private readonly List<Task> _running = new();
    private readonly StringBuilder _logBuffer = new();
    private int _nextTaskId = 1;

    private AppSettings _settings;
    private AppServices? _services;
    private HttpClientProvider _provider = null!;
    private MirrorPool _pool = null!;
    private GitHubApiClient _api = null!;
    private ContentResolver _resolver = null!;

    public MainViewModel()
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _settings = _store.Load();

        Tasks = new ObservableCollection<DownloadTaskViewModel>();
        Assets = new ObservableCollection<AssetItemViewModel>();
        LogLines = new ObservableCollection<string>();
        MirrorOptions = new ObservableCollection<string>();

        // 资产勾选状态变化要驱动底部那段「已选 N 个 / 开始下载」的提示。
        // 元素是增删的（换目录、重新解析），所以集合变化时要把新元素也挂上订阅，
        // 并且在移除时解绑 —— 不然后面这些旧对象会一直被这个 VM 引用着。
        Assets.CollectionChanged += (_, e) =>
        {
            if (e.OldItems != null)
                foreach (AssetItemViewModel a in e.OldItems)
                    a.PropertyChanged -= OnAssetPropertyChanged;

            if (e.NewItems != null)
                foreach (AssetItemViewModel a in e.NewItems)
                    a.PropertyChanged += OnAssetPropertyChanged;

            OnPropertyChanged(nameof(AssetsSelectionText));
        };
        SelectAllAssetsCommand = new RelayCommand(() => SetAllAssets(true));
        ScrollParseToTopCommand = new RelayCommand(RequestScrollParseToTop);
        ToggleFavoritesCollapsedCommand =
            new RelayCommand(() => FavoritesCollapsed = !FavoritesCollapsed);
        RenameFavoriteCommand = new RelayCommand(RenameFavorite);
        ClearAllFavoritesCommand = new RelayCommand(ClearAllFavorites);
        RemoveFavoriteCommand = new RelayCommand(RemoveFavorite);
        // 折叠状态在 setter 里会写回设置，所以恢复时必须绕过 setter 直接赋字段，
        // 否则启动时会立刻把"上次的收起状态"当成一次用户操作再存一遍。
        _favoritesCollapsed = _settings.FavoritesCollapsed;

        // 命令
        ParseCommand = new AsyncRelayCommand(ParseAsync,
            () => InputText.Trim().Length > 0 && !IsParsing)
        { OnError = e => _ = LogAsync("解析失败：" + e.Message, true) };
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, CanDownload)
        { OnError = e => _ = LogAsync("下载失败：" + e.Message, true) };
        ClearInputCommand = new RelayCommand(ClearInput, () => InputText.Length > 0);
        BrowseDirCommand = new RelayCommand(BrowseDirectory);
        OpenDirCommand = new RelayCommand(OpenDownloadDir);
        CancelAllCommand = new RelayCommand(CancelAll);
        ClearFinishedCommand = new RelayCommand(ClearFinished);
        OpenTaskCommand = new RelayCommand(OpenTaskFolder);
        SelectAllAssetsCommand = new RelayCommand(() => SetAllAssets(true));
        SelectNoAssetsCommand = new RelayCommand(() => SetAllAssets(false));
        SaveSettingsCommand = new RelayCommand(SaveSettingsFromUi);
        CancelTaskCommand = new RelayCommand(CancelTask);
        NavigateCommand = new RelayCommand(Navigate);
        ToggleLogCommand = new RelayCommand(() => ShowLog = !ShowLog);
        UseSampleCommand = new RelayCommand(UseSample);
        PauseTaskCommand = new RelayCommand(PauseTask);
        ResumeTaskCommand = new RelayCommand(ResumeTask);
        DeleteTaskCommand = new RelayCommand(DeleteTask);
        OpenFileCommand = new RelayCommand(OpenFile);
        RevealFileCommand = new RelayCommand(RevealFile);
        CopyLinkCommand = new RelayCommand(CopyLink);
        ToggleFavoriteCommand = new RelayCommand(ToggleFavorite);
        UseHistoryCommand = new RelayCommand(UseHistory);
        DeleteHistoryCommand = new RelayCommand(DeleteHistory);
        ClearHistoryCommand = new RelayCommand(ClearHistory);
        OpenFolderNavCommand = new RelayCommand(OpenFolderNav);
        ExportLogsCommand = new RelayCommand(ExportLogs);
        ResetTuningCommand = new RelayCommand(ResetTuning);
        UseFavoriteCommand = new RelayCommand(UseFavorite);
        UseClipboardHintCommand = new RelayCommand(UseClipboardHint);
        DismissClipboardHintCommand = new RelayCommand(DismissClipboardHint);
        OpenDownloadDirCommand = new RelayCommand(() => OpenInExplorer(_settings.DownloadDir));
        SetCloseBehaviorCommand = new RelayCommand(SetCloseBehavior);
        ApplySeedColorCommand = new RelayCommand(ApplySeedColor);
        BrowseCacheDirCommand = new RelayCommand(BrowseCacheDirectory);
        RefreshDebugCommand = new RelayCommand(RefreshDebug);
        EnterCloudDirCommand = new AsyncRelayCommand(
            p => EnterCloudDirAsync(p as AssetItemViewModel));
        CloudBackCommand = new AsyncRelayCommand(CloudBackAsync);
        ClearDebugCommand = new RelayCommand(ClearDebug);
        OpenCacheDirCommand = new RelayCommand(() =>
            OpenInExplorer(string.IsNullOrWhiteSpace(_settings.CacheDir)
                ? _settings.DownloadDir
                : _settings.CacheDir!));
        OpenDbDirCommand = new RelayCommand(() =>
        {
            if (_services == null) return;
            OpenInExplorer(Path.GetDirectoryName(_services.Database.FilePath));
        });
        CloudLoginCommand = new AsyncRelayCommand(CloudLoginAsync);
        CloudLogoutCommand = new AsyncRelayCommand(CloudLogoutAsync);
        ExportBackupCommand = new AsyncRelayCommand(ExportBackupAsync);
        ImportBackupCommand = new AsyncRelayCommand(ImportBackupAsync);
        ResetCredentialsCommand = new AsyncRelayCommand(ResetCredentialsAsync);

        _ = InitializeAsync();
    }

    // ===================== 绑定属性 =====================

    public ObservableCollection<DownloadTaskViewModel> Tasks { get; }
    public ObservableCollection<HistoryEntry> History { get; } = new();
    public ObservableCollection<FavoriteEntry> Favorites { get; } = new();
    public ObservableCollection<CloudAccountRow> CloudAccounts { get; } = new();
    public ObservableCollection<AssetItemViewModel> Assets { get; }
    public ObservableCollection<string> LogLines { get; }
    public ObservableCollection<string> MirrorOptions { get; }

    // ===================== 仓库文件树浏览 =====================

    /// <summary>当前浏览的仓库目录内容（GitHub 来源可用）。</summary>
    public ObservableCollection<RepoTreeEntry> BrowserEntries { get; } = new();

    private bool _browserOpen;
    public bool BrowserOpen
    {
        get => _browserOpen;
        private set => SetProperty(ref _browserOpen, value);
    }

    private string _browserPath = "";
    /// <summary>当前目录路径（空 = 仓库根）。</summary>
    public string BrowserPath
    {
        get => _browserPath;
        private set
        {
            if (!SetProperty(ref _browserPath, value)) return;
            OnPropertyChanged(nameof(BrowserPathText));
            OnPropertyChanged(nameof(CanBrowserBack));
        }
    }

    public string BrowserPathText => string.IsNullOrEmpty(BrowserPath)
        ? (_lastResolved?.HasRepo == true ? $"{_lastResolved.Owner}/{_lastResolved.Repo} /" : "—")
        : $"{_lastResolved?.Owner}/{_lastResolved?.Repo} / {BrowserPath}";

    public bool CanBrowserBack => !string.IsNullOrEmpty(BrowserPath);

    /// <summary>能否浏览文件树：只有 GitHub 来源且解析出了具体仓库。</summary>
    public bool CanBrowseRepo => _lastResolved?.HasRepo == true;

    private readonly Stack<string> _browserStack = new();

    private RelayCommand? _browseRepoCommand;
    public RelayCommand BrowseRepoCommand =>
        _browseRepoCommand ??= new RelayCommand(() => _ = OpenBrowserAsync(""));

    private RelayCommand? _browserBackCommand;
    public RelayCommand BrowserBackCommand =>
        _browserBackCommand ??= new RelayCommand(() => _ = BrowserBackAsync());

    private RelayCommand? _browserCloseCommand;
    public RelayCommand BrowserCloseCommand =>
        _browserCloseCommand ??= new RelayCommand(() =>
        {
            BrowserOpen = false;
            BrowserEntries.Clear();
            _browserStack.Clear();
            BrowserPath = "";
        });

    /// <summary>打开仓库文件树（<paramref name="path"/> 为空 = 根目录）。</summary>
    private async Task OpenBrowserAsync(string path, bool pushHistory = true)
    {
        if (_services == null || _lastResolved is not { HasRepo: true } src) return;

        try
        {
            if (pushHistory && BrowserOpen) _browserStack.Push(BrowserPath);

            BrowserOpen = true;
            BrowserPath = path;
            BrowserEntries.Clear();

            var entries = await _services.GitHubBrowser.ListDirectoryAsync(
                src.Owner, src.Repo, path, src.GitRef.Length == 0 ? null : src.GitRef)
                .ConfigureAwait(true);

            // 目录在前、文件在后；同类按名字排，读起来接近资源管理器
            foreach (var e in entries
                         .OrderByDescending(x => x.IsDirectory)
                         .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                BrowserEntries.Add(e);

            await LogAsync($"浏览 {src.Owner}/{src.Repo}{(path.Length == 0 ? "" : "/" + path)}" +
                           $" · {entries.Count} 项").ConfigureAwait(true);
        }
        catch (Exception e)
        {
            await LogAsync($"打开仓库文件树失败：{e.Message}", true).ConfigureAwait(true);
            BrowserOpen = false;
        }
    }

    private async Task BrowserBackAsync()
    {
        if (_browserStack.Count == 0)
        {
            await OpenBrowserAsync("", pushHistory: false).ConfigureAwait(true);
            return;
        }
        string prev = _browserStack.Pop();
        await OpenBrowserAsync(prev, pushHistory: false).ConfigureAwait(true);
    }

    /// <summary>点击树中的条目：目录进入，文件加入下载队列。</summary>
    private void BrowserActivate(object? param)
    {
        if (param is not RepoTreeEntry entry || _lastResolved is not { HasRepo: true } src) return;

        if (entry.IsDirectory)
        {
            _ = OpenBrowserAsync(entry.Path);
            return;
        }

        // 文件：拼 raw 链接入队。GitRef 为空时用 HEAD，raw 端点会解析到默认分支。
        string ghRef = src.GitRef.Length == 0 ? "HEAD" : src.GitRef;
        string url = GitHubUrlParser.RawUrl(src.Owner, src.Repo, ghRef, entry.Path);
        Enqueue(url, FileNamePolicy.SanitizeFileName(entry.Name), headers: null);
        _ = LogAsync($"已加入队列：{entry.Path}");
    }

    private RelayCommand? _browserActivateCommand;
    public RelayCommand BrowserActivateCommand =>
        _browserActivateCommand ??= new RelayCommand(BrowserActivate);

    // ===================== README 预览（GFM 渲染） =====================

    /// <summary>渲染后的 README 块（供 ItemsControl 用 DataTemplate 逐块呈现）。</summary>
    public ObservableCollection<MdBlockView> ReadmeBlocks { get; } = new();

    private bool _readmeOpen;
    public bool ReadmeOpen
    {
        get => _readmeOpen;
        private set => SetProperty(ref _readmeOpen, value);
    }

    private string _readmeError = "";
    public string ReadmeError
    {
        get => _readmeError;
        private set => SetProperty(ref _readmeError, value);
    }

    private AsyncRelayCommand? _showReadmeCommand;
    public AsyncRelayCommand ShowReadmeCommand =>
        _showReadmeCommand ??= new AsyncRelayCommand(ShowReadmeAsync);

    private RelayCommand? _closeReadmeCommand;
    public RelayCommand CloseReadmeCommand =>
        _closeReadmeCommand ??= new RelayCommand(() =>
        {
            ReadmeOpen = false;
            ReadmeBlocks.Clear();
            ReadmeError = "";
        });

    /// <summary>
    /// 拉取并渲染当前仓库的 README。
    ///
    /// <para><b>为什么现在才接</b>：<see cref="MarkdownParser"/> 早就写好并有断言覆盖
    /// （标题/代码/列表/引用/表格/行内格式/图片），但从来没有消费者 ——
    /// 解析能力躺在那里，用户看到的仍是纯文本。这里把它接到界面上。</para>
    /// </summary>
    private async Task ShowReadmeAsync()
    {
        if (_services == null || _lastResolved is not { HasRepo: true } src) return;

        try
        {
            ReadmeError = "";
            ReadmeBlocks.Clear();
            ReadmeOpen = true;

            string? md = await _services.GitHubBrowser
                .GetReadmeAsync(src.Owner, src.Repo,
                    src.GitRef.Length == 0 ? null : src.GitRef)
                .ConfigureAwait(true);

            if (string.IsNullOrEmpty(md))
            {
                ReadmeError = "这个仓库没有可显示的 README";
                return;
            }

            // 相对路径的图片要改写成 raw 绝对地址，否则界面上加载不出来。
            string refName = src.GitRef.Length == 0 ? "HEAD" : src.GitRef;
            md = MarkdownParser.RewriteRelativeUrls(md, src.Owner, src.Repo, refName);

            var parser = new MarkdownParser();
            foreach (var b in parser.Parse(md))
                ReadmeBlocks.Add(MdBlockView.From(b));

            await LogAsync($"README 已渲染：{src.Owner}/{src.Repo} · {ReadmeBlocks.Count} 块")
                .ConfigureAwait(true);
        }
        catch (Exception e)
        {
            ReadmeError = "README 加载失败：" + e.Message;
            await LogAsync(ReadmeError, true).ConfigureAwait(true);
        }
    }

    private string _inputText = "";
    public string InputText
    {
        get => _inputText;
        set
        {
            if (!SetProperty(ref _inputText, value)) return;

            // 输入变了，旧的解析结果就不该再能下载（否则会下错东西）。
            //
            // ★ 判据必须覆盖「所有已解析状态」，而不只是 `_lastResolved != null`：
            //   磁力与网盘都不写 `_lastResolved`（前者置空、后者走 `_lastCloud`），
            //   只用它判断的话，改了输入框仍然能按旧磁力/旧网盘结果下载 —— 会下错内容。
            bool stale = HasParsed || _lastResolved != null || _lastCloud != null
                         || _magnetUrl.Length > 0;
            if (stale && _resolvedFor != value.Trim())
            {
                _lastResolved = null;
                _lastCloud = null;
                _cloudCtx = null;
                _magnetUrl = "";
                HasParsed = false;
                ShowAssets = false;
                Assets.Clear();
                ParsedTitle = "等待解析";
                ParsedKind = "";
                ParsedNote = "输入已变化，请重新解析";
                OnPropertyChanged(nameof(IsMagnetSource));
                OnPropertyChanged(nameof(IsCloudSource));
                OnPropertyChanged(nameof(CanCloudBack));
            }

            ParseCommand.RaiseCanExecuteChanged();
            DownloadCommand.RaiseCanExecuteChanged();
            // 清空按钮只在有内容时可用（空了再点没有意义）
            ClearInputCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>解析结果对应的输入文本（用于判断缓存是否仍然有效）。</summary>
    private string _resolvedFor = "";

    private string _downloadDir = "";
    public string DownloadDir
    {
        get => _downloadDir;
        set { if (SetProperty(ref _downloadDir, value)) { _settings.DownloadDir = value; } }
    }

    private string _parsedTitle = "等待输入链接";
    public string ParsedTitle { get => _parsedTitle; set => SetProperty(ref _parsedTitle, value); }

    private string _parsedNote = "";
    public string ParsedNote { get => _parsedNote; set => SetProperty(ref _parsedNote, value); }

    private string _parsedKind = "";
    public string ParsedKind { get => _parsedKind; set => SetProperty(ref _parsedKind, value); }

    private bool _hasParsed;
    public bool HasParsed { get => _hasParsed; set => SetProperty(ref _hasParsed, value); }

    /// <summary>
    /// 上一次成功解析的结果缓存。
    /// 下载时直接复用，避免为拿 URL 再调一次 API ——
    /// 那不仅浪费一次配额，第二次若失败还会让本来能下的任务失败。
    /// </summary>
    private ResolvedContent? _lastResolved;

    // ===== 网盘来源状态 =====
    /// <summary>最近一次网盘解析结果；null 表示当前是 GitHub 来源。</summary>
    private SourceResolution? _lastCloud;

    /// <summary>网盘分享上下文（取直链、浏览子目录都要它）。</summary>
    private CloudShareContext? _cloudCtx;

    /// <summary>
    /// 目录栈：存每层进入前的 CurrentDirectoryId，用于「返回上级」。
    /// 用栈而不是记路径字符串，是因为各平台的目录 ID 形态完全不同
    /// （夸克是 fid、115 是 cid、百度是 path 编码），只能原样回放。
    /// </summary>
    private readonly Stack<string?> _cloudDirStack = new();

    public bool IsCloudSource => _lastCloud != null;
    public bool CanCloudBack => _cloudDirStack.Count > 0;

    /// <summary>当前所在位置（面包屑）。</summary>
    public string CloudPathText => _lastCloud == null
        ? ""
        : _cloudDirStack.Count == 0
            ? _lastCloud.Title
            : $"{_lastCloud.Title} › {_cloudDirStack.Count} 层";

    /// <summary>资产面板标题：网盘是「分享内容」，GitHub 是「选择要下载的资产」。</summary>
    public string AssetsTitle => IsCloudSource ? "分享内容（点目录进入，勾选文件下载）" : "选择要下载的资产";

    /// <summary>
    /// 列表底部的「已选 N / 共 M」提示。
    ///
    /// <para>放在按钮旁边，是因为用户勾选完之后视线就在列表底部 ——
    /// 让他不用滑回顶部也能确认自己选了几项、有没有选错。</para>
    /// </summary>
    public string AssetsSelectionText
    {
        get
        {
            int total = Assets.Count(a => a.IsFile);
            int picked = Assets.Count(a => a.IsFile && a.IsSelected);
            if (total == 0) return "";

            // 选中 0 项时把话说清楚，否则用户点了「开始下载」没反应会以为是 bug
            return picked == 0
                ? $"未选中文件（共 {total} 个）—— 勾选后才能下载"
                : $"已选 {picked} / {total} 个文件";
        }
    }

    /// <summary>勾选状态变化的统一回调：只需要刷新那段提示文案。</summary>
    private void OnAssetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssetItemViewModel.IsSelected))
            OnPropertyChanged(nameof(AssetsSelectionText));
    }

    /// <summary>
    /// 请求把解析页滚回顶部。
    ///
    /// <para>VM 不持有 ScrollViewer（那是 View 的事），所以这里只发一个事件，
    /// 由窗口订阅后执行真正的滚动。这样 ViewModel 仍然可以脱离 WPF 单测。</para>
    /// </summary>
    public event Action? ScrollParseToTopRequested;

    private void RequestScrollParseToTop() => ScrollParseToTopRequested?.Invoke();

    private bool _showAssets;
    public bool ShowAssets { get => _showAssets; set => SetProperty(ref _showAssets, value); }

    private string _statusText = "就绪";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private bool _hasTasks;
    public bool HasTasks { get => _hasTasks; set => SetProperty(ref _hasTasks, value); }

    private bool _isFavorited;
    public bool IsFavorited
    {
        get => _isFavorited;
        set
        {
            if (!SetProperty(ref _isFavorited, value)) return;
            // 按钮文字跟着变，否则点了没反应的样子
            OnPropertyChanged(nameof(FavoriteButtonText));
        }
    }

    /// <summary>收藏按钮上的文字：随状态切换，让"已收藏"这件事看得见。</summary>
    public string FavoriteButtonText => IsFavorited ? "已收藏" : "收藏";

    private string _historyKeyword = "";
    public string HistoryKeyword
    {
        get => _historyKeyword;
        set { if (SetProperty(ref _historyKeyword, value)) RefreshHistory(); }
    }

    /// <summary>
    /// 是否有解析历史（用于空态提示）。
    ///
    /// <para>必须显式通知：<see cref="History"/> 是 ObservableCollection，
    /// 它的变化不会自动让这个计算属性失效 —— 不调用 OnPropertyChanged 的话，
    /// 界面会一直停在"还没有历史"的提示上。</para>
    /// </summary>
    public bool HasHistory => History.Count > 0;

    /// <summary>历史为空时显示占位文案；有关键字搜索时换个说法。</summary>
    public string HistoryEmptyText => string.IsNullOrWhiteSpace(HistoryKeyword)
        ? "还没有解析记录。粘贴链接解析一次，这里会留下痕迹，方便以后直接复用（连提取码一起记住）。"
        : "没有匹配的记录。";

    /// <summary>
    /// 是否有收藏（用于空态提示）。
    ///
    /// <para>与 <see cref="HasHistory"/> 同理：ObservableCollection 的元素变化
    /// 不会让计算属性自动失效，必须在 <c>RefreshFavorites()</c> 里手动通知。</para>
    /// </summary>
    public bool HasFavorites => Favorites.Count > 0;

    public string FavoritesEmptyText =>
        "还没有收藏。解析出结果后点「收藏」，常用链接会固定在这里，不会随历史被挤掉。";

    private bool _favoritesCollapsed;

    /// <summary>
    /// 收藏区是否收起。
    ///
    /// <para><b>为什么要能收</b>：侧边栏只有 204px 宽，收藏区占的是导航下方的
    /// 全部剩余高度。收藏十几条之后，导航被挤到上面、下面全是链接，
    /// 而用户多数时候只是路过侧边栏去点导航 —— 这时候收藏是干扰。</para>
    ///
    /// <para>默认展开：新用户还没有收藏，展开也占不到地方；
    /// 一旦收起，选择会被记住（见 SettingsStore.FavoritesCollapsed）。</para>
    /// </summary>
    public bool FavoritesCollapsed
    {
        get => _favoritesCollapsed;
        set
        {
            if (!SetProperty(ref _favoritesCollapsed, value)) return;
            _settings.FavoritesCollapsed = value;
            OnPropertyChanged(nameof(FavoritesListVisible));
            OnPropertyChanged(nameof(FavoritesChevronAngle));
            OnPropertyChanged(nameof(FavoritesToggleTip));
        }
    }

    /// <summary>收藏列表是否可见（有收藏且未收起）。</summary>
    public bool FavoritesListVisible => HasFavorites && !FavoritesCollapsed;

    /// <summary>折叠三角的角度：展开朝下 90°，收起朝右 0°。</summary>
    public double FavoritesChevronAngle => FavoritesCollapsed ? 0 : 90;

    public string FavoritesToggleTip => FavoritesCollapsed ? "展开收藏" : "收起收藏";

    public RelayCommand ToggleFavoritesCollapsedCommand { get; }

    // ===== 下载调试页 =====

    /// <summary>调试页当前选中的任务。</summary>
    private DownloadTaskViewModel? _debugTask;
    public DownloadTaskViewModel? DebugTask
    {
        get => _debugTask;
        set
        {
            if (!SetProperty(ref _debugTask, value)) return;
            RefreshDebugEvents();
            OnPropertyChanged(nameof(DebugTaskTitle));
        }
    }

    public string DebugTaskTitle => _debugTask == null
        ? "未选择任务"
        : $"{_debugTask.FileName} · 任务 #{_debugTask.TaskId}";

    /// <summary>事件流（倒序，最新的在最上面）。</summary>
    public ObservableCollection<string> DebugEvents { get; } = new();

    private string _resourceSnapshotText = "";
    public string ResourceSnapshotText
    {
        get => _resourceSnapshotText;
        set => SetProperty(ref _resourceSnapshotText, value);
    }

    /// <summary>刷新事件流 + 资源快照。</summary>
    private void RefreshDebugEvents()
    {
        DebugEvents.Clear();
        if (_services == null || _debugTask == null)
        {
            ResourceSnapshotText = "—";
            return;
        }

        var events = _services.DownloadDebug.Snapshot(_debugTask.TaskId);
        // 倒序：最新事件在最上面，省得往下滚
        foreach (var e in Enumerable.Reverse(events).Take(400))
            DebugEvents.Add(e.Text);

        if (DebugEvents.Count == 0)
            DebugEvents.Add("（该任务还没有事件。事件流只在「下载调试」开启时记录。）");

        var snap = Core.Diagnostics.ResourceSnapshot.Capture(
            inflight: _debugTask.CanCancel ? _settings.Threads : 0,
            limit: _settings.Threads,
            activeTasks: RunningCount);

        ResourceSnapshotText = snap.Describe() +
                               $"\n已下载 {Humanize.Size(_debugTask.Done)} / {Humanize.Size(_debugTask.Size)}" +
                               $" · {_debugTask.SpeedText} · {_debugTask.StateText}";
    }

    private System.Windows.Threading.DispatcherTimer? _debugTimer;

    /// <summary>
    /// 只在调试页可见时开定时刷新。
    /// 一直刷会白白读事件队列 + 抓资源快照（后者要查询进程句柄数与 GC 计数），
    /// 用户看不见的地方没理由付这个开销。
    /// </summary>
    private void SyncDebugTimer()
    {
        if (Page == AppPage.Debug)
        {
            _debugTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            // 避免重复挂 Tick
            _debugTimer.Stop();
            _debugTimer.Tick -= OnDebugTick;
            _debugTimer.Tick += OnDebugTick;
            if (_debugTask == null && Tasks.Count > 0) DebugTask = Tasks[0];
            RefreshDebugEvents();
            _debugTimer.Start();
        }
        else
        {
            _debugTimer?.Stop();
        }
    }

    private void OnDebugTick(object? sender, EventArgs e) => TickDebug();

    private void RefreshDebug(object? param) => RefreshDebugEvents();

    private void ClearDebug(object? param)
    {
        _services?.DownloadDebug.ClearAll();
        DebugEvents.Clear();
        DebugEvents.Add("（已清空）");
        _ = LogAsync("已清空下载调试事件");
    }

    /// <summary>运行中持续刷新（调试页可见时才刷，避免无谓开销）。</summary>
    private void TickDebug()
    {
        if (Page != AppPage.Debug || _services == null) return;
        if (_debugTask == null && Tasks.Count > 0) DebugTask = Tasks[0];
        RefreshDebugEvents();
    }

    // ===== 储存位置 =====

    /// <summary>分片临时文件目录。空 = 与下载目录相同。</summary>
    public string CacheDir
    {
        get => _settings.CacheDir ?? "";
        set
        {
            if (_settings.CacheDir == value) return;
            _settings.CacheDir = value.Length == 0 ? null : value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CacheDirText));
        }
    }

    /// <summary>缓存目录的说明文案（空值时显示「与下载目录相同」）。</summary>
    public string CacheDirText => string.IsNullOrWhiteSpace(_settings.CacheDir)
        ? "与下载目录相同"
        : _settings.CacheDir!;

    /// <summary>数据库文件位置（只读展示 —— 改它要重启并迁移数据，不值得自动化）。</summary>
    public string DatabasePathText => _services?.Database.FilePath ?? "—";

    /// <summary>
    /// 设置页底部的版本与协议说明。
    ///
    /// <para><b>为什么要显示在界面上</b>：本程序是 AGPL-3.0 授权的自由软件，
    /// 分发时有义务让使用者知道自己的权利（可修改、可再分发、需源码）。
    /// 只写在 README 里不够 —— 拿到 exe 的人未必会去看仓库。</para>
    ///
    /// <para>版本号取自程序集（csproj 的 &lt;Version&gt;），不硬编码，避免发版忘改。</para>
    /// </summary>
    public string AboutText
    {
        get
        {
            // 版本号取自程序集（唯一来源是 csproj 的 <Version>），
            // 不写死任何 fallback —— 否则升版本时漏改就会显示旧版本号。
            string? ver = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3);
            return string.IsNullOrEmpty(ver)
                ? "YunX Desktop　·　Copyright (C) 2026 Lyaco"
                : $"YunX Desktop v{ver}　·　Copyright (C) 2026 Lyaco";
        }
    }

    /// <summary>分片级 SHA-256 校验开关。</summary>
    public bool VerifyChunks
    {
        get => _settings.VerifyChunks;
        set { _settings.VerifyChunks = value; OnPropertyChanged(); }
    }

    /// <summary>完成后整文件 SHA-256（要完整读一遍文件，大文件有明显代价）。</summary>
    public bool VerifyHash
    {
        get => _settings.VerifyHash;
        set { _settings.VerifyHash = value; OnPropertyChanged(); }
    }

    // ===== 自定义种子色 =====

    /// <summary>
    /// 预设种子色。只给 6 个有辨识度的选项，不做完整取色器 ——
    /// 桌面工具的配色一旦给了任意色轮，用户很容易调出难以阅读的组合。
    /// </summary>
    public IReadOnlyList<SeedSwatch> SeedPresets { get; } = new List<SeedSwatch>
    {
        new("#4C8BF5", "默认蓝"),
        new("#22C1A4", "青绿"),
        new("#7C6BF0", "紫罗兰"),
        new("#E0733D", "橙"),
        new("#E0446E", "洋红"),
        new("#5FA83C", "草绿"),
    };

    /// <summary>当前种子色（HEX）。设置后在运行中立即生效。</summary>
    public string SeedColor
    {
        get => _settings.SeedColor ?? Themes.SeedPalette.ToHex(Themes.SeedPalette.DefaultSeed);
        set
        {
            if (_settings.SeedColor == value) return;
            _settings.SeedColor = value;
            _store.Save(_settings);
            OnPropertyChanged();
            Themes.SeedPalette.Apply(Application.Current, Themes.SeedPalette.Parse(value));
        }
    }

    private void ApplySeedColor(object? param)
    {
        string hex = param as string ?? "";
        if (hex.Length == 0) return;
        SeedColor = hex;
        _ = LogAsync($"强调色已切换为 {hex}");
    }

    // ===== 关闭行为 =====

    /// <summary>ask / exit / tray。默认 ask：不替用户做决定，也不静默改变行为。</summary>
    public string CloseBehavior
    {
        get => _settings.CloseBehavior;
        set
        {
            if (_settings.CloseBehavior == value) return;
            _settings.CloseBehavior = value;
            _store.Save(_settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CloseBehaviorLabel));
            OnPropertyChanged(nameof(IsCloseAsk));
            OnPropertyChanged(nameof(IsCloseExit));
            OnPropertyChanged(nameof(IsCloseTray));
        }
    }

    /// <summary>关闭行为的显示文案（ComboBox 用）。</summary>
    public string CloseBehaviorLabel
    {
        get => CloseBehavior switch
        {
            "exit" => "直接退出",
            "tray" => "最小化到托盘继续下载",
            _ => "每次询问",
        };
        set
        {
            CloseBehavior = value switch
            {
                var x when x.StartsWith("直接") => "exit",
                var x when x.StartsWith("最小化") => "tray",
                _ => "ask",
            };
        }
    }

    public string[] CloseBehaviorLabels { get; } =
        { "每次询问", "直接退出", "最小化到托盘继续下载" };

    public bool IsCloseAsk => CloseBehavior == "ask";
    public bool IsCloseExit => CloseBehavior == "exit";
    public bool IsCloseTray => CloseBehavior == "tray";

    private void SetCloseBehavior(object? param)
    {
        string v = (param as string ?? "").ToLowerInvariant();
        if (v is "ask" or "exit" or "tray") CloseBehavior = v;
    }

    // ===== 剪贴板检测 =====
    private System.Windows.Threading.DispatcherTimer? _clipTimer;
    private string _clipSeenHash = "";
    private string _clipPending = "";

    /// <summary>检测到链接时由 View 订阅（用于做滑入动画）。</summary>
    public event Action? ClipboardHintShown;

    /// <summary>
    /// 单个任务下载完成（窗口订阅后用托盘气泡提示）。
    ///
    /// <para>参数：(文件名, 大小文本)。由窗口决定是否真的弹 ——
    /// 窗口活跃时不打扰，只有切到后台才提示。</para>
    /// </summary>
    public event Action<string, string>? TaskCompleted;

    /// <summary>
    /// 非阻断式告警（右下角浮层）。参数：(标题, 细节)。
    ///
    /// <para><b>为什么需要它</b>：解析失败可以用模态弹窗拦住用户，
    /// 但下载失败不行 —— 批量下载时可能同时失败好几个，
    /// 连续弹模态框会把用户逼疯。这类"告知但不阻断"的错误走浮层。</para>
    /// </summary>
    public event Action<string, string>? AlertRaised;

    /// <summary>
    /// 弹一条浮层告警。切回 UI 线程安全，可在任意线程调用。
    /// </summary>
    /// <param name="title">标题：一行说清是什么失败了</param>
    /// <param name="detail">细节：为什么、怎么处理（可为空）</param>
    public void RaiseAlert(string title, string detail = "")
    {
        if (_disposed) return;
        _ui.Post(_ =>
        {
            try { AlertRaised?.Invoke(title, detail); }
            catch { /* 通知失败不该影响任何业务逻辑 */ }
        }, null);
    }

    /// <summary>
    /// 解析类失败的统一出口：弹模态框 + 写日志。
    ///
    /// <para><b>为什么解析要用模态而不是浮层</b>：解析是用户的主动操作，
    /// 失败了就什么也没有 —— 不让用户明确知道"失败了、为什么"，
    /// 他会以为是软件卡住，反复点解析。模态框强制确认，才能形成闭环。</para>
    /// </summary>
    private void ReportParseFailure(string stage, string reason)
    {
        ParsedKind = "解析失败";
        ParsedTitle = "";
        ParsedNote = reason;
        HasParsed = false;

        // 日志留全量细节（含阶段），弹窗只给用户能看懂的部分
        _ = LogAsync($"解析失败（{stage}）：{reason}", true);

        string advice = reason.Contains("超时", StringComparison.Ordinal)
            ? "\n\n可能是网络不通或线路拥塞。可以稍后重试，或在设置里换个线路 / 开启「绕过 hosts 劫持」。"
            : reason.Contains("404", StringComparison.Ordinal)
                ? "\n\n请检查仓库名、分支、文件名是否正确。私有仓库需要在设置里填 Token。"
                : reason.Contains("Token", StringComparison.Ordinal)
                    ? "\n\n请在「设置 → GitHub 访问」里填入 Personal Access Token。"
                    : "";

        Views.PromptDialog.Alert("解析失败",
            $"{reason}\n\n阶段：{stage}{advice}");
    }

    /// <summary>下载完成通知开关。</summary>
    public bool NotifyOnComplete
    {
        get => _settings.NotifyOnComplete;
        set { _settings.NotifyOnComplete = value; OnPropertyChanged(); }
    }

    private bool _clipboardHintVisible;
    public bool ClipboardHintVisible
    {
        get => _clipboardHintVisible;
        set => SetProperty(ref _clipboardHintVisible, value);
    }

    private string _clipboardHintText = "";
    public string ClipboardHintText
    {
        get => _clipboardHintText;
        set => SetProperty(ref _clipboardHintText, value);
    }

    private string _cloudStatus = "";
    public string CloudStatus { get => _cloudStatus; set => SetProperty(ref _cloudStatus, value); }

    private bool _initFailed;
    /// <summary>
    /// 初始化是否失败（服务装配阶段抛异常）。
    ///
    /// <para>失败后 <c>_services</c> 为 null，所有依赖它的刷新（任务、历史、
    /// 收藏、网盘账号）都会静默跳过 —— 界面表现为「每个页都是空的」，
    /// 而日志里只有一行。有了这个标志，页面至少能明确告诉用户去查日志。</para>
    /// </summary>
    public bool InitFailed
    {
        get => _initFailed;
        set
        {
            if (SetProperty(ref _initFailed, value))
                OnPropertyChanged(nameof(InitFailureIsCredential));
        }
    }

    private bool _initFailureIsCredential;
    /// <summary>
    /// 这次初始化失败是否**由加密凭据引起**。
    ///
    /// <para>页面上「清理加密凭据并重建」按钮只在为 true 时显示。
    /// 上一版没有这个区分，导致 SQLite 原生库缺失时也引导用户去清凭据 ——
    /// 那条路治不好，还会白白丢掉登录态。</para>
    /// </summary>
    public bool InitFailureIsCredential
    {
        get => _initFailureIsCredential;
        set => SetProperty(ref _initFailureIsCredential, value);
    }

    private string _initFailureMessage = "";
    /// <summary>初始化失败的具体原因（显示在页面上，便于用户反馈）。</summary>
    public string InitFailureMessage
    {
        get => _initFailureMessage;
        set => SetProperty(ref _initFailureMessage, value);
    }

    private string _credentialWarning = "";
    /// <summary>凭证相关的非致命告警（如「主密钥解开失败，凭证已失效」）。</summary>
    public string CredentialWarning
    {
        get => _credentialWarning;
        set
        {
            if (SetProperty(ref _credentialWarning, value))
                OnPropertyChanged(nameof(HasCredentialWarning));
        }
    }

    public bool HasCredentialWarning => !string.IsNullOrEmpty(_credentialWarning);

    // ===== 实验性设置的双向绑定（带范围校验）=====
    public int ReadBufferKb
    {
        get => _settings.ReadBufferKb;
        set { _settings.ReadBufferKb = value; OnPropertyChanged(); }
    }

    public int PreemptFloorKb
    {
        get => _settings.PreemptFloorKb;
        set { _settings.PreemptFloorKb = value; OnPropertyChanged(); }
    }

    public int PreemptAgeSec
    {
        get => _settings.PreemptAgeSec;
        set { _settings.PreemptAgeSec = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 启用 HTTP/2。
    ///
    /// <para>改这个值必须**同时**推到 <see cref="HttpClientProvider"/>，否则只是存了个设置项 ——
    /// 它曾经就是死开关：保存了、界面上也能点，但没有任何 HttpClient 读过它。</para>
    /// </summary>
    public bool EnableHttp2
    {
        get => _settings.EnableHttp2;
        set
        {
            if (_settings.EnableHttp2 == value) return;
            _settings.EnableHttp2 = value;
            if (_provider != null) _provider.EnableHttp2 = value;
            OnPropertyChanged();
            _ = LogAsync(value
                ? "已启用 HTTP/2（部分镜像可能不支持，会自动降级到 1.1）"
                : "已切回 HTTP/1.1");
        }
    }

    public int MaxConcurrentTasks
    {
        get => _settings.MaxConcurrentTasks;
        set
        {
            int clamped = Math48.Clamp(value, 1, 10);
            if (_settings.MaxConcurrentTasks == clamped) return;

            _settings.MaxConcurrentTasks = clamped;
            OnPropertyChanged();

            // 上限由 AcquireSlotAsync 每次取槽时重新读，所以这里不需要动任何等待原语 ——
            // 调大后排队任务最迟 120ms 内被放行，已经在跑的任务也不受影响。
            _ = LogAsync($"同时下载任务数：{clamped}");
        }
    }

    public bool KeepAwake
    {
        get => _settings.KeepAwake;
        set { _settings.KeepAwake = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 下载历史保留条数（已完成任务）。
    ///
    /// <para>下限 1：设 0 会让"重启后空列表"重现，用户没法区分
    /// 「没下过」和「记录丢了」。上限 1000：再多也没人翻，只是占库。</para>
    /// </summary>
    public int CompletedHistoryLimit
    {
        get => _settings.CompletedHistoryLimit;
        set
        {
            int clamped = Math48.Clamp(value, 1, 1000);
            if (_settings.CompletedHistoryLimit == clamped) return;

            _settings.CompletedHistoryLimit = clamped;
            OnPropertyChanged();

            // 调小后立即生效：不用等下一次下载完成才裁剪
            PruneCompletedHistory();
            _ = LogAsync($"下载历史保留条数：{clamped}");
        }
    }

    public bool ClipboardWatch
    {
        get => _settings.ClipboardWatch;
        set
        {
            if (_settings.ClipboardWatch == value) return;
            _settings.ClipboardWatch = value;
            OnPropertyChanged();

            // 开关必须真正启停定时器。原来的写法只改设置值，而 Timer 只在初始化时
            // 启动一次 —— 于是「启动时关着、后来打开」剪贴板检测永远不生效，
            // 反过来「后来关掉」也仍在每 1.2 秒空转。
            ApplyClipboardWatch();
        }
    }

    public bool DiagnosticsEnabled
    {
        get => _settings.DiagnosticsEnabled;
        set
        {
            _settings.DiagnosticsEnabled = value;
            OnPropertyChanged();
            if (_services != null) _services.Diagnostics.Enabled = value;
        }
    }

    public bool DownloadDebugEnabled
    {
        get => _settings.DownloadDebugEnabled;
        set
        {
            _settings.DownloadDebugEnabled = value;
            OnPropertyChanged();
            if (_services != null)
            {
                _services.DownloadDebug.Enabled = value;
                if (!value) _services.DownloadDebug.ClearAll();
            }
        }
    }

    public string[] KeyProtectionOptions { get; } =
        { "portable", "machine", "user" };

    public string KeyProtectionLabel
    {
        get => _settings.KeyProtectionLabel;
        set
        {
            // setter 收到的是显示文本，映射回内部值
            string v = value switch
            {
                var x when x.StartsWith("宽松") => "portable",
                var x when x.StartsWith("较为严格") => "user",
                _ => "machine",
            };
            if (_settings.KeyProtection == v) return;
            _settings.KeyProtection = v;
            try { _services?.Cipher.ChangeLevel(v == "portable"
                ? Core.Security.KeyProtectionLevel.Portable
                : v == "user"
                    ? Core.Security.KeyProtectionLevel.UserBound
                    : Core.Security.KeyProtectionLevel.MachineBound); }
            catch { /* 等级切换失败不阻断 */ }
            OnPropertyChanged();
            OnPropertyChanged(nameof(KeyProtectionText));
        }
    }

    public string[] KeyProtectionLabels { get; } =
        { "宽松（密钥明文，可跨设备）", "普通（绑定本机）", "较为严格（DPAPI，绑定当前 Windows 用户）" };

    public string KeyProtectionText => _settings.KeyProtectionLabel;

    private AppPage _page = AppPage.Parse;

    /// <summary>当前分区。切换时通知所有 IsXxxPage，供导航项与内容区绑定。</summary>
    public AppPage Page
    {
        get => _page;
        set
        {
            if (!SetProperty(ref _page, value)) return;
            OnPropertyChanged(nameof(IsParsePage));
            OnPropertyChanged(nameof(IsDownloadsPage));
            OnPropertyChanged(nameof(IsAccountsPage));
            OnPropertyChanged(nameof(IsDebugPage));
            OnPropertyChanged(nameof(IsSettingsPage));
            OnPropertyChanged(nameof(IsFavoritesPage));
            SyncDebugTimer();
        }
    }

    public bool IsParsePage => _page == AppPage.Parse;
    public bool IsDownloadsPage => _page == AppPage.Downloads;
    public bool IsAccountsPage => _page == AppPage.Accounts;
    public bool IsDebugPage => _page == AppPage.Debug;
    public bool IsSettingsPage => _page == AppPage.Settings;
    public bool IsFavoritesPage => _page == AppPage.Favorites;

    private bool _showLog;
    /// <summary>日志抽屉是否展开（默认收起，需要排查时才看）。</summary>
    public bool ShowLog { get => _showLog; set => SetProperty(ref _showLog, value); }

    /// <summary>侧栏「下载」上的运行中数量徽标。</summary>
    public bool RunningBadgeVisible => RunningCount > 0;
    public string RunningBadgeText => RunningCount > 99 ? "99+" : RunningCount.ToString();

    private int _runningCount;
    public int RunningCount
    {
        get => _runningCount;
        private set
        {
            if (!SetProperty(ref _runningCount, value)) return;
            OnPropertyChanged(nameof(RunningBadgeVisible));
            OnPropertyChanged(nameof(RunningBadgeText));
            OnPropertyChanged(nameof(StatusDotBrush));
        }
    }

    /// <summary>状态点：有任务在跑时亮起来，空闲时灰。</summary>
    public System.Windows.Media.Brush StatusDotBrush => _runningCount > 0
        ? new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x3F, 0xB9, 0x50))
        : new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x5C, 0x66, 0x75));

    /// <summary>状态栏右侧的简短网络模式（完整描述太长，占地方）。</summary>
    public string NetModeShort => _provider?.Describe() switch
    {
        "系统代理" => "代理",
        null => "",
        _ => "直连",
    };

    private string _netModeText = "联网模式：检测中…";
    public string NetModeText
    {
        get => _netModeText;
        set
        {
            if (!SetProperty(ref _netModeText, value)) return;
            OnPropertyChanged(nameof(NetModeShort));
        }
    }

    private double _totalPercent;
    public double TotalPercent { get => _totalPercent; set => SetProperty(ref _totalPercent, value); }

    private string _totalSpeedText = "";
    public string TotalSpeedText { get => _totalSpeedText; set => SetProperty(ref _totalSpeedText, value); }

    private string? _tokenError;
    public string? TokenError { get => _tokenError; set => SetProperty(ref _tokenError, value); }

    private string _apiQuotaText = "尚未查询";

    /// <summary>
    /// API 配额状态文案（设置页显示）。
    ///
    /// <para><b>为什么值得占一行 UI</b>：GitHub 匿名 API 只有 60 次/小时，
    /// 用完就 403，而用户从「解析失败」的报错里根本看不出是这个原因。
    /// 提前把剩多少摆出来，用户就知道该配 Token 了。</para>
    /// </summary>
    public string ApiQuotaText
    {
        get => _apiQuotaText;
        private set => SetProperty(ref _apiQuotaText, value);
    }

    /// <summary>配额偏低时的提示色标记（剩余 < 10 次）。</summary>
    public bool ApiQuotaLow => _apiQuotaLow;
    private bool _apiQuotaLow;

    /// <summary>从 API 客户端刷新配额显示。没有 Token 且没调过 API 时会显示「未使用」。*</summary>
    private void RefreshApiQuota()
    {
        var rl = _api?.RateLimit;
        if (rl is not { } r)
        {
            // 一次都没调过 API（新版解析根本不碰 API）—— 这是好事，说清楚
            ApiQuotaText = _api?.HasToken == true
                ? "尚未使用（每次解析会消耗 1 次）"
                : "未使用（解析不消耗配额，下载也不消耗）";
            _apiQuotaLow = false;
            OnPropertyChanged(nameof(ApiQuotaLow));
            return;
        }

        string reset = r.ResetAt is { } t ? $"，{t:HH:mm} 重置" : "";
        ApiQuotaText = $"剩余 {r.Remaining} / {r.Limit} 次{reset}";
        _apiQuotaLow = r.Remaining <= 10;
        OnPropertyChanged(nameof(ApiQuotaLow));
    }

    // ---- 设置面板绑定 ----
    public int Threads
    {
        get => _settings.Threads;
        set { _settings.Threads = Math48.Clamp(value, 1, 32); OnPropertyChanged(); }
    }

    public bool ResumeEnabled
    {
        get => _settings.Resume;
        set { _settings.Resume = value; OnPropertyChanged(); }
    }

    public bool PreemptEnabled
    {
        get => _settings.PreemptSlowChunks;
        set { _settings.PreemptSlowChunks = value; OnPropertyChanged(); }
    }

    public bool OpenFolderWhenDone
    {
        get => _settings.OpenFolderWhenDone;
        set { _settings.OpenFolderWhenDone = value; OnPropertyChanged(); }
    }

    public int SpeedLimitKb
    {
        get => _settings.SpeedLimitKb;
        set { _settings.SpeedLimitKb = Math.Max(0, value); OnPropertyChanged(); }
    }

    public bool BypassHosts
    {
        get => _settings.BypassHosts;
        set { _settings.BypassHosts = value; OnPropertyChanged(); }
    }

    public string GitHubToken
    {
        get => _settings.GitHubToken;
        set { _settings.GitHubToken = value.Trim(); OnPropertyChanged(); }
    }

    public string[] NetModes { get; } = { "自动检测", "直连（忽略代理）", "系统代理" };

    public string NetModeLabel
    {
        get => _settings.NetMode switch
        {
            "direct" => "直连（忽略代理）",
            "proxy" => "系统代理",
            _ => "自动检测",
        };
        set
        {
            _settings.NetMode = value switch
            {
                "直连（忽略代理）" => "direct",
                "系统代理" => "proxy",
                _ => "auto",
            };
            OnPropertyChanged();
        }
    }

    public string MirrorLabel
    {
        get => _settings.MirrorId;
        set { _settings.MirrorId = value; OnPropertyChanged(); }
    }

    // ===================== 解析进度 =====================

    private bool _isParsing;
    private string _parseStage = "";

    /// <summary>是否正在解析。为 true 时界面覆盖一层遮罩，挡住重复点击。</summary>
    public bool IsParsing
    {
        get => _isParsing;
        private set
        {
            if (!SetProperty(ref _isParsing, value)) return;
            OnPropertyChanged(nameof(ShowParseOverlay));
            ParseCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 解析阶段文案（「正在探测线路…」这类）。
    ///
    /// <para><b>为什么必须有</b>：一次解析要经过仓库探测、API 抓资产、线路测速
    /// 几个阶段，合计 1~3 秒。期间界面完全不动，用户无法区分「在干活」和「卡死」。
    /// 只有转圈还不够 —— 圈子转三秒也会让人怀疑，配上具体阶段才安心。</para>
    /// </summary>
    public string ParseStage
    {
        get => _parseStage;
        private set => SetProperty(ref _parseStage, value);
    }

    /// <summary>遮罩是否可见（保留给 XAML 绑定，语义比直接绑 IsParsing 清楚）。</summary>
    public bool ShowParseOverlay => IsParsing;

    /// <summary>设置阶段文案。切回 UI 线程安全，可在后台回调里调用。</summary>
    private void SetStage(string stage)
    {
        if (_disposed) return;
        _ui.Post(_ => ParseStage = stage, null);
    }

    // ===================== 命令 =====================

    public AsyncRelayCommand ParseCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public RelayCommand ClearInputCommand { get; }
    public RelayCommand BrowseDirCommand { get; }
    public RelayCommand OpenDirCommand { get; }
    public RelayCommand CancelAllCommand { get; }
    public RelayCommand ClearFinishedCommand { get; }
    public RelayCommand OpenTaskCommand { get; }
    public RelayCommand SelectAllAssetsCommand { get; }
    public RelayCommand SelectNoAssetsCommand { get; }

    /// <summary>把解析页滚回顶部（配合列表底部的「开始下载」使用）。</summary>
    public RelayCommand ScrollParseToTopCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand CancelTaskCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand ToggleLogCommand { get; }
    public RelayCommand UseSampleCommand { get; }
    public RelayCommand PauseTaskCommand { get; }
    public RelayCommand ResumeTaskCommand { get; }
    public RelayCommand DeleteTaskCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand RevealFileCommand { get; }
    public RelayCommand CopyLinkCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }

    /// <summary>给收藏起自定义名（留空恢复显示网址）。</summary>
    public RelayCommand RenameFavoriteCommand { get; }

    /// <summary>清空全部收藏。</summary>
    public RelayCommand ClearAllFavoritesCommand { get; }

    /// <summary>删除单条收藏（收藏页的行内按钮用）。</summary>
    public RelayCommand RemoveFavoriteCommand { get; }
    public RelayCommand UseHistoryCommand { get; }
    public RelayCommand DeleteHistoryCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand OpenFolderNavCommand { get; }
    public RelayCommand ExportLogsCommand { get; }
    public RelayCommand ResetTuningCommand { get; }
    public RelayCommand UseFavoriteCommand { get; }
    public RelayCommand UseClipboardHintCommand { get; }
    public RelayCommand DismissClipboardHintCommand { get; }
    public RelayCommand OpenDownloadDirCommand { get; }
    public RelayCommand SetCloseBehaviorCommand { get; }
    public RelayCommand ApplySeedColorCommand { get; }
    public RelayCommand BrowseCacheDirCommand { get; }
    public RelayCommand RefreshDebugCommand { get; }
    public AsyncRelayCommand EnterCloudDirCommand { get; }
    public AsyncRelayCommand CloudBackCommand { get; }
    public RelayCommand ClearDebugCommand { get; }
    public RelayCommand OpenCacheDirCommand { get; }
    public RelayCommand OpenDbDirCommand { get; }
    public AsyncRelayCommand CloudLoginCommand { get; }
    public AsyncRelayCommand CloudLogoutCommand { get; }
    public AsyncRelayCommand ExportBackupCommand { get; }
    public AsyncRelayCommand ImportBackupAsyncCommand => ImportBackupCommand;
    public AsyncRelayCommand ImportBackupCommand { get; }

    /// <summary>清理损坏的加密凭据并重建（凭证告警条上的操作）。</summary>
    public AsyncRelayCommand ResetCredentialsCommand { get; }

    // ===================== 初始化 =====================

    private async Task InitializeAsync()
    {
        DownloadDir = _settings.DownloadDir;

        // ---- 装配服务（数据库 / 加密 / 网盘 / 诊断）----
        try
        {
            // ★ 必须放到线程池上构造。
            //
            //   AppServices 构造里会 Initialize 凭证加密器，而它要做
            //   **PBKDF2 210,000 轮**（当前安全推荐强度）来派生主密钥 ——
            //   这是纯 CPU 密集的同步操作，实测占用数百毫秒到 1 秒。
            //   直接在 UI 线程上 new，整个窗口会僵住：表现为切到「账号」页
            //   看到一片空白、几秒后才冒出列表。
            //
            //   构造完再回到 UI 线程继续（ConfigureAwait(true) 是当前上下文）。
            _services = await Task.Run(() => new AppServices(_store, _settings))
                .ConfigureAwait(true);
        }
        catch (Exception e)
        {
            // ★ 这里原来只记 e.Message，把栈全丢了。
            //
            //   实际踩到的后果：net48 版启动时 AesGcmCompat 抛
            //   「BCryptEncrypt 失败（NTSTATUS 0xC000000D）」，日志只有这一句，
            //   完全看不出是 CredentialCipher.Initialize → Wrap → AesGcmEncrypt 的哪一步。
            //   而 catch 之后直接 return，意味着账号页/历史/收藏全都不会填充 ——
            //   用户看到的是「账号页一直空白」，跟「初始化失败」看不出任何关联。
            //
            //   所以：记完整栈，并且把失败状态暴露到 UI（CloudStatus），
            //   别再让整个初始化静默中止。
            await LogAsync($"初始化服务失败：{e}", true);
            InitFailed = true;
            InitFailureMessage = e.Message;
            // 分类：只有真的是加密问题才允许引导用户清理凭据。
            // （上一版没分类，SQLite 原生库缺失时也劝人去清 credential.key。）
            InitFailureIsCredential = ClassifyInitFailure(e) == InitFailureKind.Credential;

            // ★ 失败必须让用户看见，并且给出可操作的出路。
            //
            //   之前这里只是 return —— 用户看到的是「账号页一直空白」，
            //   既不知道原因，也没有任何补救入口，只能一直卡着。
            //   弹窗按失败类别给不同的主按钮（见 PromptInitFailureAsync），
            //   「清理凭据」只对加密类失败才出现。
            await PromptInitFailureAsync(e, retry: true).ConfigureAwait(true);
            return;
        }

        // 服务已建起来了：清掉上一次的失败标志（走「重试初始化」成功时必须复位，
        // 否则页面上的红色失败条会一直挂着，反而让用户以为还没修好）。
        InitFailed = false;
        InitFailureMessage = "";

        // 主密钥被重置 = 旧凭证全废。这不是致命错误（程序能继续跑），
        // 但用户必须知道「为什么账号全部退出了」，否则会以为是自己没登录。
        if (_services.Cipher.MasterKeyWasReset)
        {
            await LogAsync("主密钥无法解开，已生成新密钥；此前保存的凭证已失效。", true);
            CredentialWarning = "之前保存的网盘凭证已失效（主密钥无法解开），请重新登录。";
            // 没有 Toast 实例可用（它挂在 MainWindow 上），靠绑定 CredentialWarning
            // 在账号页顶部显示常驻提示 —— 比一闪而过的浮层更适合「需要用户行动」的信息。
        }

        await LogAsync("正在初始化网络环境…");

        // ★ 先装配**纯本地**的数据，再做联网探测。
        //
        //   这些都不依赖网络：任务恢复、解析历史、收藏、网盘账号列表（读本地凭证表）。
        //   而下面的 SetModeAsync → AutoDetectAsync 会**并发探测直连与代理**，
        //   超时上限是秒级 —— 把它们放在前面，用户切到「账号」页会看到一片空白，
        //   持续几秒才突然冒出列表，像是卡住了。
        //   顺序调整后：界面一进来就有内容，网络探测在后台慢慢完成。
        RestorePersistedTasks();
        RefreshHistory();
        RefreshFavorites();
        BuildCloudAccountRows();

        if (_settings.BypassHosts)
        {
            try
            {
                await HostsBypass.EnableAsync(m => LogAsync(m)).ConfigureAwait(true);
            }
            catch (Exception e)
            {
                await LogAsync($"hosts 检测异常：{e.Message}", true);
            }
        }

        _provider = _services.Http;
        // 把持久化的 HTTP/2 开关推到 provider，否则重启后设置形同虚设
        _provider.EnableHttp2 = _settings.EnableHttp2;
        try
        {
            await _provider.SetModeAsync(_settings.NetworkMode, m => LogAsync(m))
                .ConfigureAwait(true);
        }
        catch (Exception e)
        {
            await LogAsync($"联网模式探测失败：{e.Message}", true);
        }
        NetModeText = "联网模式：" + _provider.Describe();

        RebuildNetworkStack();
        RefreshMirrorOptions();

        await LogAsync("就绪。粘贴链接开始（支持 GitHub 与夸克/UC/迅雷/百度/139/123 网盘）。");

        // 剪贴板检测：默认开启，可在设置里关掉
        ApplyClipboardWatch();
    }

    /// <summary>
    /// 初始化失败时的交互式恢复流程。
    ///
    /// <para><b>为什么必须弹窗</b>：初始化失败会让 <c>_services</c> 为 null，
    /// 于是账号、历史、收藏、任务恢复全部静默跳过 —— 界面上只是「一片空白」，
    /// 用户既不知道原因，也没有任何补救入口。这类失败绝大多数是加密凭据坏了
    /// （主密钥解不开），而它**完全可以自愈**：删掉 credential.key 重建即可，
    /// 代价是已保存的网盘登录态失效（用户重新登录就行）。</para>
    ///
    /// <para>关键点：不要求用户重启程序。点「清理并重建」后直接重试初始化。</para>
    /// </summary>
    /// <summary>
    /// 清理损坏的加密凭据并重建（凭证告警条上的按钮）。
    ///
    /// <para>与 <see cref="PromptInitFailureAsync"/> 的区别：那个用在「初始化直接失败、
    /// 程序起不来」的场景；这个用在「程序起来了，但主密钥解不开、旧凭证已失效」的场景 ——
    /// 后者不致命，但用户需要一条明确的补救路径，而不是干看着账号全是「未登录」。</para>
    /// </summary>
    private async Task ResetCredentialsAsync()
    {
        string keyPath = _services?.Cipher.KeyFilePath
            ?? Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), "YunX-Desktop-CS", "credential.key");

        int pick = Views.PromptDialog.ChooseWithPrimary(
            "清理加密凭据",
            "这会删除本机的加密密钥文件并重新生成：\n" +
            keyPath + "\n\n" +
            "效果：账号列表恢复正常，但**所有已保存的网盘登录状态会失效**，需要重新登录。\n" +
            "已下载的文件、下载历史、收藏不受影响。",
            0, "清理并重建", "取消");
        if (pick != 0) return;

        try
        {
            if (_services != null)
            {
                _services.Cipher.ResetKeyFile();
                // 凭证表里存的都是旧密钥加密的密文，重建后一律解不开 ——
                // 留着只会让每个平台在读取时各抛一次异常，干脆清空。
                _services.Credentials.ClearAll();
                BuildCloudAccountRows();
            }
            else
            {
                if (File.Exists(keyPath)) File.Delete(keyPath);
                // 走了这条说明服务没建起来，需要重新初始化整条链路
                await InitializeAsync().ConfigureAwait(true);
                return;
            }

            CredentialWarning = "";
            await LogAsync("加密凭据已重建，请重新登录各网盘。");
        }
        catch (Exception ex)
        {
            await LogAsync("重建凭据失败：" + ex.Message, true);
            Views.PromptDialog.Alert("重建失败",
                ex.Message + "\n\n如果文件被占用，可以手动删除后重启程序：\n" + keyPath);
        }
    }

    /// <summary>初始化失败的类别 —— 决定弹窗文案和「主按钮该做什么」。</summary>
    private enum InitFailureKind
    {
        /// <summary>加密凭据（credential.key）损坏或解不开。清理重建有效。</summary>
        Credential,
        /// <summary>数据库打不开（原生 interop 缺失 / 文件被占用）。清理凭据无效。</summary>
        Database,
        /// <summary>其它未知原因。不要引导用户去清理凭据。</summary>
        Other,
    }

    /// <summary>
    /// 按异常特征判断失败类别。
    ///
    /// <para>判据全部来自实际踩过的错：</para>
    /// <list type="bullet">
    /// <item><c>CryptographicException</c> / 消息里带 BCrypt、认证标签、credential.key
    ///   → 加密问题，清理有效。</item>
    /// <item>消息里带 <c>SQLite.Interop</c>、<c>SQLite</c>、<c>yunx.db</c> 或异常类型是
    ///   <c>DllNotFoundException</c>/<c>BadImageFormatException</c>
    ///   → 数据库/原生库问题，清理凭据**完全无效**（这条是实机踩到的）。</item>
    /// </list>
    /// </summary>
    private static InitFailureKind ClassifyInitFailure(Exception error)
    {
        // 逐层展开 AggregateException / InnerException —— AppServices 是在
        // Task.Run 里构造的，真实异常常被包一层。
        for (Exception? e = error; e != null; e = e.InnerException)
        {
            string msg = e.Message ?? "";

            if (e is DllNotFoundException or BadImageFormatException ||
                msg.Contains("SQLite", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("yunx.db", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("数据库", StringComparison.Ordinal))
            {
                return InitFailureKind.Database;
            }

            if (e is System.Security.Cryptography.CryptographicException ||
                msg.Contains("BCrypt", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("认证标签", StringComparison.Ordinal) ||
                msg.Contains("credential.key", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("密钥", StringComparison.Ordinal))
            {
                return InitFailureKind.Credential;
            }

            if (e is AggregateException agg && agg.InnerException != null)
            {
                var inner = ClassifyInitFailure(agg.InnerException);
                if (inner != InitFailureKind.Other) return inner;
            }
        }
        return InitFailureKind.Other;
    }

    private async Task PromptInitFailureAsync(Exception error, bool retry)
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YunX-Desktop-CS");
        string keyPath = Path.Combine(dataDir, "credential.key");

        // ★ 必须先判断「这次失败到底是不是加密引起的」。
        //
        //   上一版这里无条件把任何初始化异常都说成「加密凭据损坏」，
        //   主按钮也是「清理并重建」。实机踩到的后果：
        //   真实原因是 SQLite.Interop.dll 找不到（原生库没随包发布），
        //   弹窗却让用户去清理 credential.key —— 那条路永远治不好，
        //   而且用户会真的丢掉网盘登录态。误导性文案比没有文案更糟。
        var kind = ClassifyInitFailure(error);

        string detail;
        string primary;
        string secondary;

        switch (kind)
        {
            case InitFailureKind.Credential:
                detail =
                    "程序初始化失败，账号列表、下载历史、收藏都无法加载。\n\n" +
                    "原因：" + error.Message + "\n\n" +
                    "加密凭据文件损坏或无法解开：\n" + keyPath + "\n\n" +
                    "可以清理它并重建，程序会立刻恢复正常。\n" +
                    "代价：已保存的网盘登录状态会失效，需要重新登录。";
                primary = "清理并重建";
                secondary = "打开数据目录";
                break;

            case InitFailureKind.Database:
                detail =
                    "程序初始化失败：数据库无法打开。\n\n" +
                    "原因：" + error.Message + "\n\n" +
                    "数据目录：\n" + dataDir + "\n\n" +
                    "这通常是原生数据库组件缺失、或数据库文件被其他程序占用。\n" +
                    "如果刚升级过版本，请先确认程序目录里带有「原生数据库组件」。";
                primary = "打开数据目录";
                secondary = "知道了";
                break;

            default:
                detail =
                    "程序初始化失败，账号列表、下载历史、收藏都无法加载。\n\n" +
                    "原因：" + error.Message + "\n\n" +
                    "数据目录：\n" + dataDir + "\n\n" +
                    "这不是加密凭据的问题，清理凭据不会有帮助。\n" +
                    "建议把上面的原因反馈给开发者，日志可在数据目录下的 diagnostic_logs 里找到。";
                primary = "打开数据目录";
                secondary = "知道了";
                break;
        }

        int choice = Views.PromptDialog.ChooseWithPrimary(
            "初始化失败", detail, 0, primary, secondary);

        // 按钮语义随类别变化，不能再写死 choice==1 就是「打开目录」。
        // 只有 Credential 类的主按钮才是「清理并重建」，其余两类的主按钮是「打开数据目录」。
        if (kind != InitFailureKind.Credential)
        {
            if (choice == 0) OpenFolder(dataDir);
            return;
        }

        if (choice == 1)
        {
            OpenFolder(dataDir);
            return;
        }

        if (choice != 0) return;

        // ---- 清理并重建 ----
        try
        {
            if (File.Exists(keyPath)) File.Delete(keyPath);
            await LogAsync("已删除损坏的加密凭据，正在重建…");
        }
        catch (Exception ex)
        {
            Views.PromptDialog.Alert("清理失败",
                "无法删除凭据文件，可能被其它程序占用：\n\n" + ex.Message +
                "\n\n请手动删除后重启程序：\n" + keyPath);
            return;
        }

        if (retry)
        {
            await LogAsync("正在重试初始化…");
            await InitializeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>在资源管理器里打开（或定位到）某个路径。</summary>
    private static void OpenFolder(string path)
    {
        try
        {
            // 目录直接打开；文件则选中它 —— 用户点「打开数据目录」时想看的
            // 多半是目录里有哪些文件，所以统一打开目录更合适。
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"")
                { UseShellExecute = true });
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
                { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe",
                    "\"" + Path.GetDirectoryName(path) + "\"") { UseShellExecute = true });
        }
        catch { /* 打不开就算了，不能因为这点事再抛一次 */ }
    }

    /// <summary>构建「账号」页的网盘列表（含登录状态）。</summary>
    private void BuildCloudAccountRows()
    {
        if (_services == null) return;

        CloudAccounts.Clear();
        foreach (var (platform, name, login) in _services.CloudDrives.Describe())
        {
            // 单个平台的凭证读取可能失败（数据库被锁、密钥不匹配）——
            // 不能因此让整个列表空掉或抛到启动流程里。
            string? saved = null;
            try { saved = _services.GetCredential(platform); }
            catch { /* 读不到就当未登录 */ }

            var row = new CloudAccountRow
            {
                Platform = platform,
                DisplayName = name,
                Login = login,
            };
            bool loggedIn = !string.IsNullOrWhiteSpace(saved);
            row.IsLoggedIn = loggedIn;
            row.Status = loggedIn ? "已登录" : "未登录";
            CloudAccounts.Add(row);
        }
        CloudStatus = $"已登录 {CloudAccounts.Count(x => x.IsLoggedIn)} / {CloudAccounts.Count}";
    }

    /// <summary>按当前设置重建线路池与解析器（设置变更后调用）。</summary>
    private void RebuildNetworkStack()
    {
        var mirrors = SettingsStore.BuildMirrors(_settings);
        _pool = new MirrorPool(mirrors, _provider.CreateClient(forDownload: true), _provider)
        {
            OnlyMirrorId = _settings.MirrorId == "auto" ? null : _settings.MirrorId,
        };

        _api = new GitHubApiClient(_provider,
            () => string.IsNullOrWhiteSpace(_settings.GitHubToken) ? null : _settings.GitHubToken)
        {
            Log = m => LogAsync("[API] " + m),
            // 每次拿到响应头就刷新配额显示（不需要额外请求）
            RateLimitChanged = () => _ui.Post(_ => RefreshApiQuota(), null),
        };
        RefreshApiQuota();
        _api.OnUnauthorized = () =>
        {
            _settings.GitHubToken = "";
            _store.Save(_settings);
            _ui.Post(_ =>
            {
                OnPropertyChanged(nameof(GitHubToken));
                TokenError = "Token 无效或已过期，已自动清空并改为匿名访问。";
            }, null);
        };

        _resolver = new ContentResolver(_api, _provider)
        {
            Log = m => LogAsync("[解析] " + m),
            // 内部阶段（探测仓库 / 抓资产 / 测线路）实时反映到遮罩上，
            // 否则用户只能看着一个不动的「正在连接 GitHub…」等好几秒。
            Progress = SetStage,
        };
    }

    private void RefreshMirrorOptions()
    {
        MirrorOptions.Clear();
        MirrorOptions.Add("auto");
        foreach (var m in _pool.AllMirrors.Where(x => x.Enabled))
            MirrorOptions.Add(m.Id);
    }

    // ===================== 解析 =====================

    /// <summary>
    /// 网络栈（线路池/API/解析器）是否已就绪。
    ///
    /// <para><see cref="InitializeAsync"/> 里先装配本地数据、再做联网探测、最后才
    /// <see cref="RebuildNetworkStack"/>。这中间有个窗口期，用户完全可能已经粘贴链接
    /// 点了「解析」—— 那时这三个字段还是 <c>null!</c>，直接访问就是空引用异常。
    /// 依赖它们的入口必须先问一句。</para>
    /// </summary>
    private bool NetworkStackReady => _pool != null && _api != null && _resolver != null;

    private async Task ParseAsync()
    {
        string input = InputText.Trim();
        if (input.Length == 0) return;

        // ★ 整个解析过程套一层遮罩 + 阶段文案。
        //   解析要经过仓库探测 / API 抓资产 / 线路测速，合计 1~3 秒；
        //   这段时间界面完全不动，用户无法区分「在干活」和「卡死」。
        //   finally 里一定会收掉遮罩 —— 包括异常和取消路径，否则界面会永久被挡住。
        IsParsing = true;
        SetStage("正在识别链接…");

        // ★ 整体超时预算。
        //
        //   单看每一步都有超时（每个 HTTP 请求 8~100 秒），但组合起来可能非常久：
        //   探测 + 抓页面 + 测 4~8 条线路，任一环节卡住，遮罩就会一直挂着。
        //   用户看到的是"永远在转圈"，比明确报错糟糕得多。
        //   这里给整个流程一个硬上限，到点就抛 ParseTimeoutException，
        //   由下面的 catch 弹窗告知 —— 宁可报"超时"，也不让界面无限等待。
        using var budget = new CancellationTokenSource(ParseBudget);

        try
        {
            await ParseCoreAsync(input, budget.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            // 预算耗尽 = 超时（区别于用户主动取消：那个会被 OperationCanceledException
            // 的另一个分支接住，且 budget.IsCancellationRequested 为 false）
            ReportParseFailure("整体耗时",
                $"解析超过 {ParseBudget.TotalSeconds:0} 秒仍未完成。");
        }
        catch (OperationCanceledException)
        {
            // 用户自己取消的，不需要告警（他刚点的取消，再弹一个框很蠢）
            ParsedKind = "已取消";
            ParsedNote = "解析已取消";
        }
        catch (ParseTimeoutException ex)
        {
            // 超时单独一类：文案要告诉用户"可以重试"，否则他会以为链接坏了
            ReportParseFailure("网络请求", "请求超时：" + ex.Message);
        }
        catch (ParseException ex)
        {
            // 解析类错误（链接不支持、资产取不到）是用户可理解的，不该弹成"崩溃"
            ReportParseFailure("识别内容", ex.Message);
        }
        catch (Exception ex)
        {
            // 兜底：未预期的异常也必须让用户知道，同时把完整堆栈写进日志
            ReportParseFailure("未知错误",
                $"解析时发生未预期的错误：{ex.Message}");
        }
        finally
        {
            IsParsing = false;
            ParseStage = "";
            ParseCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>解析主体。遮罩与异常收口在 <see cref="ParseAsync"/> 里统一处理。</summary>
    /// <summary>整个解析流程的时间预算（秒）。超过就判超时并告警。</summary>
    private static readonly TimeSpan ParseBudget = TimeSpan.FromSeconds(45);

    private async Task ParseCoreAsync(string input, CancellationToken budgetCt = default)
    {
        ParsedKind = "解析中…";
        ParsedTitle = "";
        ParsedNote = "";
        HasParsed = false;
        ShowAssets = false;
        Assets.Clear();
        _lastCloud = null;
        _cloudCtx = null;
        _cloudDirStack.Clear();
        OnPropertyChanged(nameof(IsCloudSource));
        OnPropertyChanged(nameof(CanCloudBack));
        OnPropertyChanged(nameof(CloudPathText));

        // 磁力优先：它的 scheme 特征最明确（magnet:），不可能与其它来源混淆。
        // 单独走一条识别路径，因为它不是 HTTP 下载 —— 由 Gopeed 引擎负责。
        if (MagnetLink.IsMagnet(input))
        {
            SetStage("正在读取磁力元数据…");
            ApplyMagnet(input);
            DownloadCommand.RaiseCanExecuteChanged();
            return;
        }

        // 下面就要用到线路池/解析器了。启动阶段联网探测期间它们还是 null，
        // 此时用户点了「解析」会直接空引用 —— 明确告诉他还差几秒，比崩掉好。
        if (!NetworkStackReady)
        {
            ParsedKind = "";
            ParsedTitle = "正在初始化网络…";
            ParsedNote = "线路池还在准备中（首次启动通常几秒），请稍候再点解析。";
            HasParsed = false;
            await LogAsync("网络栈尚未就绪，请稍候再试").ConfigureAwait(true);
            return;
        }

        // 网盘优先：网盘链接的域名特征比 GitHub 更明确，
        // 而且粘一个夸克链接却报「仓库不存在（404）」是很容易让人放弃的体验。
        SetStage("正在识别来源…");
        if (await TryCloudAsync(input).ConfigureAwait(true))
        {
            DownloadCommand.RaiseCanExecuteChanged();
            return;
        }

        // 解析器内部还会依次经历「仓库探测 → 抓资产列表 → 线路测速」，
        // 它通过 Log 回调把细节报上来；这里先把粗粒度阶段换成「连接中」。
        SetStage("正在连接 GitHub…");
        // 预算 token 必须传进去：否则外壳超时了，内部的 HTTP 请求还在跑，
        // 遮罩虽然收了，后台仍在耗资源、还可能把结果写回已失效的状态
        var content = await _resolver.ResolveAsync(input, _pool, budgetCt).ConfigureAwait(true);
        SetStage("正在整理结果…");
        _lastResolved = content;

        // ★ 记一条解析历史。
        //   HistoryRepository.Add 早就写好了（还带 60 秒去重窗口），但**没有任何生产调用**，
        //   只有测试在调 —— 结果「解析历史」页永远是空的，用户完全感知不到这个功能存在。
        //   写库失败不能影响解析结果，所以单独 try。
        try
        {
            _services?.History.Add(input, content.Kind.ToString(),
                content.Title ?? "", passcode: "");

            // ★ 写完必须立刻刷新列表。
            //   只写库不刷 UI 的话，界面上还是旧内容 —— 用户新解析的链接要重启才看得见，
            //   看起来就像"历史功能坏了"。这一行曾漏掉，只补了写入。
            RefreshHistory();
        }
        catch (Exception hx)
        {
            _ = LogAsync($"记录解析历史失败：{hx.Message}", true);
        }
        _lastCloud = null;
        _cloudCtx = null;
        _cloudDirStack.Clear();
        OnPropertyChanged(nameof(IsCloudSource));
        OnPropertyChanged(nameof(CanCloudBack));
        _resolvedFor = input;

        // 换了链接就要重新判断收藏状态，否则上一个链接的「已收藏」
        // 会一直挂在按钮上，看起来像这个新链接也被收藏了。
        try { IsFavorited = _services?.Favorites.Exists(input) ?? false; }
        catch { IsFavorited = false; }

        #if NET48
        // net48 的 string.Split 没有单个 string 的重载，只有 char/char[]/string[]
        ParsedKind = (content.Title ?? "").Split(new[] { " · " }, StringSplitOptions.None)[0];
#else
        ParsedKind = (content.Title ?? "").Split(" · ")[0];
#endif
        ParsedTitle = content.Title ?? "";
        int n = content.Items.Count;
        ParsedNote = n > 1
            ? $"{n} 个文件 · {content.Note}"
            : $"{content.Note}";
        HasParsed = true;

        if (content.IsMulti)
        {
            foreach (var (url, name, size) in content.Items)
                Assets.Add(new AssetItemViewModel { Name = name, Url = url, Size = size });
            ShowAssets = true;
        }

        await LogAsync($"解析成功：{content.Title}", false);
        DownloadCommand.RaiseCanExecuteChanged();
    }

    // ===================== 网盘来源 =====================

    /// <summary>
    /// 尝试按网盘解析。返回 true 表示「已处理」（成功，或已给出网盘专属错误）。
    ///
    /// <para><b>为什么失败也返回 true</b>：如果输入确实是一个网盘链接（域名被某个适配器识别），
    /// 那么失败原因只能是「缺提取码 / 提取码错 / 分享失效 / 需要登录」这类网盘特有的问题，
    /// 这时回落去解析 GitHub 只会得到「仓库不存在（404）」—— 对用户是彻底的误导。
    /// 只有「看起来完全不像网盘」的输入才交给 GitHub。</para>
    /// </summary>
    private async Task<bool> TryCloudAsync(string input, CancellationToken ct = default)
    {
        if (_services == null) return false;

        // 纯本地判断，不发网络请求
        var detected = _services.CloudDrives.Detect(input);
        bool looksCommand = ShareTextParser.LooksLikeChineseCommand(input);
        if (detected == null && !looksCommand) return false;

        try
        {
            _services.Sources.Log = m => _ = LogAsync("[网盘] " + m);
            var res = await _services.Sources.ResolveAsync(input).ConfigureAwait(true);

            if (res == null || res.CloudContext == null)
            {
                // 口令没解析出分享：这确实不像网盘，交回 GitHub 也无意义，直接提示。
                // 这是用户主动操作的结果，用弹窗 —— 只写日志等于让他对着空界面发呆。
                ReportParseFailure("识别网盘口令",
                    "口令未匹配到任何分享，该资源可能已失效。");
                return true;   // 已处理（弹窗告知），不再交给 GitHub 分支
            }

            _cloudDirStack.Clear();
            ApplyCloud(res, input);
            await LogAsync($"解析成功：{res.Title} · {res.Note}");
            return true;
        }
        catch (PasscodeRequiredException ex)
        {
            ShowCloudError("需要提取码", ex.Message,
                "在链接后面补上「提取码：xxxx」再解析，或直接粘贴含提取码的整段分享文本");
            return true;
        }
        catch (NotLoggedInException ex)
        {
            ShowCloudError("需要登录", ex.Message,
                "去「账号」页登录后重试（支持从本机浏览器一键导入 Cookie）");
            return true;
        }
        catch (CloudException ex)
        {
            ShowCloudError("解析失败", ex.Message, "");
            return true;
        }
        catch (Exception ex)
        {
            ShowCloudError("解析异常", ex.Message, "");
            return true;
        }
    }

    private void ShowCloudError(string kind, string message, string hint)
    {
        _lastCloud = null;
        _cloudCtx = null;
        _cloudDirStack.Clear();
        HasParsed = false;
        ShowAssets = false;
        Assets.Clear();

        ParsedKind = kind;
        ParsedTitle = message;
        ParsedNote = hint;

        OnPropertyChanged(nameof(IsCloudSource));
        OnPropertyChanged(nameof(CanCloudBack));
        OnPropertyChanged(nameof(CloudPathText));
        _ = LogAsync(message, true);
    }

    /// <summary>把一次网盘解析结果铺到界面上。</summary>
    /// <summary>
    /// 网盘目录是否还有下一页。
    ///
    /// <para>适配器早就算出了 <c>NextCursor</c>（百度 100 条一页、139 是 200 条、123 用服务端游标），
    /// 但从来没有消费者 —— 用户只能看到首页 100 个文件，后面的永远拿不到。</para>
    /// </summary>
    public bool CanLoadMore => _lastCloud?.HasMore == true;

    private AsyncRelayCommand? _loadMoreCommand;
    public AsyncRelayCommand LoadMoreCommand =>
        _loadMoreCommand ??= new AsyncRelayCommand(LoadMoreAsync, () => CanLoadMore);

    private async Task LoadMoreAsync()
    {
        if (_lastCloud?.HasMore != true || _cloudCtx == null || _services == null) return;

        string? cursor = _lastCloud.NextCursor;
        try
        {
            await LogAsync("加载更多…").ConfigureAwait(true);

            var res = await _services.Sources
                .BrowseCloudAsync(_cloudCtx.Drive, _cloudCtx.Share,
                    _lastCloud.CurrentDirectoryId, cursor)
                .ConfigureAwait(true);

            // 追加而不是替换：这是"下一页"，不是"另一个目录"
            foreach (var it in res.Items)
            {
                // 防御：服务端可能重复返回边界条目
                if (Assets.Any(a => a.Locator == it.Locator)) continue;

                Assets.Add(new AssetItemViewModel
                {
                    Name = it.Name,
                    Url = it.DirectUrl ?? it.Locator,
                    Size = it.Size,
                    IsDirectory = it.IsDirectory,
                    Locator = it.Locator,
                    Token = it.Token,
                });
            }

            _lastCloud = res;       // 更新游标，供下一次继续翻
            OnPropertyChanged(nameof(CanLoadMore));
            LoadMoreCommand.RaiseCanExecuteChanged();

            await LogAsync($"已加载更多：{res.Items.Count} 项，共 {Assets.Count} 项")
                .ConfigureAwait(true);
        }
        catch (Exception e)
        {
            await LogAsync($"加载更多失败：{e.Message}", true).ConfigureAwait(true);
        }
    }

    // ===================== 磁力下载（Gopeed 引擎） =====================

    /// <summary>当前解析出的磁力链接（非空 = 这是一条磁力任务）。</summary>
    private string _magnetUrl = "";

    /// <summary>磁力任务是否可用：Gopeed 内核是否已安装。</summary>
    public bool IsMagnetSource => _magnetUrl.Length > 0;

    /// <summary>当前磁力的 info hash 结构是否可用（非法则不该提交给引擎）。</summary>
    public bool IsMagnetUsable =>
        _magnetUrl.Length > 0 && MagnetLink.IsStructurallyValid(_magnetUrl);

    /// <summary>
    /// 磁力链接的解析结果呈现。
    ///
    /// <para><b>为什么不在这里列文件</b>：磁力本身不含文件列表 —— 必须先连上 DHT/peers
    /// 拿到种子元数据才知道里面有什么。所以解析阶段只能给出「名字 + info hash」，
    /// 真正的文件清单要等引擎跑起来。在这里编造一个文件列表是骗人的。</para>
    /// </summary>
    private void ApplyMagnet(string url)
    {
        _magnetUrl = url;
        _lastResolved = null;
        _lastCloud = null;
        _cloudCtx = null;

        string name = MagnetLink.DisplayName(url);
        string hash = MagnetLink.InfoHash(url);

        ParsedKind = "磁力链接";
        ParsedTitle = name;
        ParsedNote = $"Info hash：{hash}。文件清单要等引擎连上 peers 拿到元数据后才能确定。";
        HasParsed = true;
        ShowAssets = false;
        Assets.Clear();

        // 磁力也记历史：否则"解析过什么磁力"一问三不知，而这个功能同样走解析入口。
        // 与 HTTP 解析那条路径保持一致：写库 + 立即刷新。
        try
        {
            _services?.History.Add(url, "Magnet", name, passcode: "");
            RefreshHistory();
        }
        catch (Exception hx)
        {
            _ = LogAsync($"记录解析历史失败：{hx.Message}", true);
        }

        // 收藏状态同样要按新链接重算（与 HTTP 路径一致）
        try { IsFavorited = _services?.Favorites.Exists(url) ?? false; }
        catch { IsFavorited = false; }

        // ★ info hash 结构不合法时直接拦下。
        //   Gopeed 底层的 anacrolix/torrent 遇到全零/残缺的 hash 会 panic，
        //   那是 Go 级 panic —— 整个引擎进程会退出，连带弄死引擎上其它正在跑的任务。
        //   在这里拒绝，比让引擎崩掉好得多。
        if (!MagnetLink.IsStructurallyValid(url))
        {
            ParsedNote += "\n\n⚠ 这个磁力链接的 info hash 无效（可能被截断，或是全零占位），" +
                          "无法解析。请确认链接完整（通常是 40 位十六进制）。";
        }

        OnPropertyChanged(nameof(IsMagnetSource));
        OnPropertyChanged(nameof(CanBrowseRepo));
        OnPropertyChanged(nameof(IsMagnetUsable));

        // 内核没装时不能只是"下载失败"，要明确告诉用户缺什么
        if (!GopeedInstalled)
        {
            ParsedNote += "\n\n⚠ 还没有安装 Gopeed 内核（磁力下载需要它）。" +
                          "可在设置页「实验性功能 → 磁力下载」里一键下载安装。";
        }
    }

    /// <summary>Gopeed 内核是否已安装。</summary>
    public bool GopeedInstalled =>
        _services != null && GopeedProvisioner.IsInstalled(GopeedDir);

    /// <summary>Gopeed 内核目录。</summary>
    private string GopeedDir =>
        Path.Combine(_services?.DataDirectory ?? "", "gopeed");

    /// <summary>已安装内核版本（未安装为空）。</summary>
    public string GopeedVersionText =>
        _services == null ? "未安装"
            : (GopeedProvisioner.InstalledVersion(GopeedDir) is { Length: > 0 } v
                ? v
                : "未安装");

    private AsyncRelayCommand? _installGopeedCommand;
    public AsyncRelayCommand InstallGopeedCommand =>
        _installGopeedCommand ??= new AsyncRelayCommand(InstallGopeedAsync);

    /// <summary>下载并安装 Gopeed 内核（带进度）。</summary>
    private async Task InstallGopeedAsync()
    {
        if (_services == null) return;

        try
        {
            GopeedInstallBusy = true;

            var provisioner = new GopeedProvisioner(_services.Http.CreateClient());
            var progress = new Progress<GopeedProvisioner.Progress>(p =>
            {
                GopeedInstallText = p.Stage switch
                {
                    GopeedProvisioner.Stage.Resolving => "正在查询最新版本…",
                    GopeedProvisioner.Stage.Downloading => p.Total > 0
                        ? $"正在下载内核… {p.Percent:0.0}%（{Humanize.Size(p.Downloaded)} / {Humanize.Size(p.Total)}）"
                        : $"正在下载内核… {Humanize.Size(p.Downloaded)}",
                    GopeedProvisioner.Stage.Extracting => "正在解包…",
                    _ => p.Message,
                };
            });

            GopeedInstallText = "正在查询最新版本…";
            var asset = await provisioner.ResolveAsync().ConfigureAwait(true);
            await LogAsync($"开始下载 Gopeed 内核 {asset.Tag}（{Humanize.Size(asset.Size)}）")
                .ConfigureAwait(true);

            // ★ 必须带上镜像候选。
            //   github.com 的 release 下载在国内经常直接连不上 ——
            //   实测：API 域名可通、拿到资产信息，但下载地址 443 超时。
            //   只给直链就等于让这个功能对国内用户不可用。
            var candidates = await _pool.OrderedUrlsAsync(asset.Url, force: false,
                m => _ = LogAsync("内核下载：" + m)).ConfigureAwait(true);
            var mirrors = candidates
                .Where(c => !c.Url.Equals(asset.Url, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Url)
                .ToList();

            if (mirrors.Count > 0)
                await LogAsync($"内核下载准备了 {mirrors.Count} 条镜像线路").ConfigureAwait(true);

            await provisioner.InstallAsync(asset, GopeedDir, progress,
                fallbackUrls: mirrors).ConfigureAwait(true);

            GopeedInstallText = $"已安装 {asset.Tag}";
            OnPropertyChanged(nameof(GopeedInstalled));
            OnPropertyChanged(nameof(GopeedVersionText));
            await LogAsync($"Gopeed 内核已安装：{asset.Tag}").ConfigureAwait(true);
        }
        catch (Exception e)
        {
            GopeedInstallText = "安装失败：" + e.Message;
            await LogAsync($"Gopeed 内核安装失败：{e.Message}", true).ConfigureAwait(true);
        }
        finally
        {
            GopeedInstallBusy = false;
        }
    }

    private bool _gopeedInstallBusy;
    public bool GopeedInstallBusy
    {
        get => _gopeedInstallBusy;
        private set { if (SetProperty(ref _gopeedInstallBusy, value)) OnPropertyChanged(nameof(CanInstallGopeed)); }
    }

    public bool CanInstallGopeed => !GopeedInstallBusy;

    private string _gopeedInstallText = "";
    public string GopeedInstallText
    {
        get => _gopeedInstallText;
        private set => SetProperty(ref _gopeedInstallText, value);
    }

    /// <summary>
    /// 启动一条磁力任务：交给 Gopeed 引擎，然后轮询进度驱动界面。
    ///
    /// <para>与内置引擎的区别：这里不经过 <see cref="DownloadEngine"/>，
    /// 因为 BT 不是 HTTP 分片下载。任务仍复用 <see cref="DownloadTaskViewModel"/>，
    /// 界面表现一致。</para>
    /// </summary>
    private async Task RunMagnetTaskAsync(DownloadTaskViewModel vm)
    {
        await AcquireSlotAsync().ConfigureAwait(true);
        string? engineTaskId = null;

        try
        {
            if (vm.Cancelled || vm.RemoveRequested) return;

            vm.Cts?.Dispose();
            vm.Cts = new CancellationTokenSource();
            var ct = vm.Cts.Token;
            vm.State = TaskState.Running;
            vm.Phase = "连接引擎";

            var engine = await EnsureGopeedEngineAsync().ConfigureAwait(true);
            if (engine == null)
                throw new InvalidOperationException(
                    "Gopeed 内核不可用。请在设置页「实验性功能 → 磁力下载」里安装。");

            vm.Phase = "获取元数据";
            await LogAsync($"磁力任务开始：{vm.FileName}").ConfigureAwait(true);

            engineTaskId = await engine.CreateTaskAsync(
                vm.Url, vm.SaveDir.Length > 0 ? vm.SaveDir : _settings.DownloadDir,
                null, ResolveThreads(vm), ct).ConfigureAwait(false);

            await LogAsync($"引擎已受理（任务 {engineTaskId}），正在连接 peers…").ConfigureAwait(true);

            // 轮询：BT 的速度/进度只能靠拉
            long lastDownloaded = 0;
            var stallSince = DateTime.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                var t = await engine.GetTaskAsync(engineTaskId, ct).ConfigureAwait(true);

                vm.Done = t.Downloaded;
                if (t.Total > 0) vm.Size = t.Total;
                vm.Speed = t.Speed;

                // 元数据到手前 total 为 0，此时不能按百分比算，只能显示"获取元数据"
                vm.Phase = t.Total > 0 ? "下载中" : "获取元数据";
                if (t.Name.Length > 0 && vm.FileName.Length == 0) vm.FileName = t.Name;

                if (t.IsDone)
                {
                    vm.State = TaskState.Done;
                    vm.Phase = DownloadPhase.Completed;
                    vm.Speed = 0;
                    await LogAsync($"磁力下载完成：{t.Name} · {Humanize.Size(t.Downloaded)}")
                        .ConfigureAwait(true);

                    // 磁力任务同样落历史：这里原先什么都没有，
                    // 连"下过什么磁力"都留不下来。
                    PersistTask(vm, force: true);
                    PruneCompletedHistory();

                    if (_settings.NotifyOnComplete)
                    {
                        try { TaskCompleted?.Invoke(t.Name, Humanize.Size(t.Downloaded)); }
                        catch { }
                    }
                    return;
                }

                if (t.IsError)
                    throw new DownloadFailedException("引擎报告任务失败（连接不到足够的 peers）");

                if (t.IsPaused)
                {
                    vm.State = TaskState.Paused;
                    vm.Speed = 0;
                    await LogAsync($"已暂停：{vm.FileName}").ConfigureAwait(true);
                    return;
                }

                // 长时间零进度：BT 找不到 peer 是常见情况，给出可行动提示而不是干等
                if (t.Downloaded != lastDownloaded)
                {
                    lastDownloaded = t.Downloaded;
                    stallSince = DateTime.UtcNow;
                }
                else if (t.Total == 0 && (DateTime.UtcNow - stallSince).TotalSeconds > 60)
                {
                    await LogAsync(
                        "已等待 60 秒仍未拿到元数据。这个磁力可能没有可用 peer，" +
                        "或者客户端网络限制了 BT 流量。", true).ConfigureAwait(true);
                    stallSince = DateTime.UtcNow;    // 不要刷屏
                }

                await Task.Delay(700, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (vm.PauseRequested)
        {
            if (engineTaskId != null && _gopeedEngine != null)
            {
                try { await _gopeedEngine.PauseAsync(engineTaskId).ConfigureAwait(true); }
                catch { /* 暂停失败就当取消 */ }
            }
            vm.PauseRequested = false;
            vm.State = TaskState.Paused;
            vm.Speed = 0;
            PersistTask(vm);
        }
        catch (OperationCanceledException)
        {
            if (engineTaskId != null && _gopeedEngine != null)
            {
                try { await _gopeedEngine.DeleteAsync(engineTaskId).ConfigureAwait(true); } catch { }
            }
            vm.State = TaskState.Cancelled;
            vm.Speed = 0;
            PersistTask(vm);
        }
        catch (Exception e)
        {
            vm.State = TaskState.Failed;
            vm.Error = e.Message;
            vm.Speed = 0;
            await LogAsync($"磁力任务失败：{vm.FileName} — {e.Message}", true).ConfigureAwait(true);
            RaiseAlert($"磁力任务失败：{vm.FileName}",
                "可能是没有可用的 peer，或网络限制了 BT 流量。");
            PersistTask(vm);
        }
        finally
        {
            ReleaseSlot();
            RecalcTotals();
        }
    }

    private GopeedEngine? _gopeedEngine;

    /// <summary>确保引擎在运行（首次调用或崩溃后都会重新启动）。</summary>
    private async Task<GopeedEngine?> EnsureGopeedEngineAsync()
    {
        if (_gopeedEngine is { IsRunning: true }) return _gopeedEngine;

        if (!GopeedInstalled) return null;

        // 引擎可能已经崩溃（Go 侧 panic）：先彻底停掉再起新的，
        // 否则会拿着一个 Process 已退出的对象去发请求，报"连接被拒绝"。
        if (_gopeedEngine != null)
        {
            await LogAsync("检测到 Gopeed 引擎已退出，正在重启…", true).ConfigureAwait(true);
            try { _gopeedEngine.Dispose(); } catch { }
            _gopeedEngine = null;
        }

        _gopeedEngine = new GopeedEngine(GopeedDir);

        try
        {
            await _gopeedEngine.StartAsync(m => _ = LogAsync(m)).ConfigureAwait(true);
            string ver = await _gopeedEngine.GetVersionAsync().ConfigureAwait(true);
            await LogAsync($"Gopeed 引擎已就绪（内核版本 {ver}）").ConfigureAwait(true);
            return _gopeedEngine;
        }
        catch (Exception e)
        {
            await LogAsync($"Gopeed 引擎启动失败：{e.Message}", true).ConfigureAwait(true);
            _gopeedEngine.Dispose();
            _gopeedEngine = null;
            return null;
        }
    }

    /// <summary>把当前磁力做成一条任务并入队。</summary>
    private async Task DownloadMagnetAsync()
    {
        if (_magnetUrl.Length == 0) return;

        // 非法 info hash 绝不能提交给引擎：会让整个 Gopeed 进程 panic 退出
        if (!MagnetLink.IsStructurallyValid(_magnetUrl))
        {
            string why = "磁力链接的 info hash 无效，无法下载。请确认链接完整（通常 40 位十六进制）。";
            await LogAsync(why, true).ConfigureAwait(true);
            // 直接弹窗：用户点了下载却什么都没发生，不给原因就是"软件无响应"
            Views.PromptDialog.Alert("磁力链接无效", why +
                "\n\n可以从发布页重新复制完整链接，或改用普通下载链接。");
            return;
        }

        if (!GopeedInstalled)
        {
            await LogAsync(
                "磁力下载需要 Gopeed 内核。请到 设置 → 实验性功能 → 磁力下载 里点击安装。", true)
                .ConfigureAwait(true);
            Page = AppPage.Settings;
            return;
        }

        // 同一磁力重复入队没有意义（引擎里会跑两份一样的任务）
        string key = MagnetLink.Normalize(_magnetUrl);
        if (Tasks.Any(t => t.State is TaskState.Running or TaskState.Queued or TaskState.Paused
                           && MagnetLink.Normalize(t.Url) == key))
        {
            await LogAsync("这个磁力已经在任务列表里了").ConfigureAwait(true);
            return;
        }

        var vm = new DownloadTaskViewModel
        {
            TaskId = _nextTaskId++,
            Url = _magnetUrl,
            FileName = MagnetLink.DisplayName(_magnetUrl),
            State = TaskState.Queued,
            SaveDir = _settings.DownloadDir,
            Platform = "magnet",
        };

        Tasks.Add(vm);
        HasTasks = true;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DownloadTaskViewModel.Percent)
                or nameof(DownloadTaskViewModel.SpeedText))
                RecalcTotals();
        };

        if (_settings.AutoSwitchToDownloads) Page = AppPage.Downloads;

        await LogAsync($"已加入磁力队列：{vm.FileName}").ConfigureAwait(true);

        var task = RunMagnetTaskAsync(vm);
        lock (_running) _running.Add(task);
        _ = task.ContinueWith(t => { lock (_running) _running.Remove(t); },
            TaskScheduler.Default);
    }

    private void ApplyCloud(SourceResolution res, string rawInput)
    {
        _lastCloud = res;
        _cloudCtx = res.CloudContext;
        _resolvedFor = rawInput;
        // 清掉 GitHub 缓存，避免「上一个 GitHub 结果的 Items」被误当成本次的下载源
        _lastResolved = null;

        ParsedKind = PlatformDisplayName(res.Platform);
        ParsedTitle = res.Title;
        ParsedNote = res.Note;
        HasParsed = true;

        Assets.Clear();
        foreach (var it in res.Items)
        {
            Assets.Add(new AssetItemViewModel
            {
                Name = it.Name,
                Url = it.DirectUrl ?? it.Locator,
                Size = it.Size,
                IsDirectory = it.IsDirectory,
                Locator = it.Locator,
                Token = it.Token,
            });
        }

        // 目录项即使只有 1 个也要展开面板 —— 用户需要点进去
        ShowAssets = Assets.Count > 1 || Assets.Any(a => a.IsDirectory);
        OnPropertyChanged(nameof(IsCloudSource));
        OnPropertyChanged(nameof(CanCloudBack));
        OnPropertyChanged(nameof(CloudPathText));
        OnPropertyChanged(nameof(AssetsTitle));
        // 换目录/换分享后分页游标变了，必须刷新「加载更多」的可用性
        OnPropertyChanged(nameof(CanLoadMore));
        LoadMoreCommand.RaiseCanExecuteChanged();
    }

    private static string PlatformDisplayName(string platform) => platform switch
    {
        Core.Cloud.CloudPlatforms.Quark => "夸克网盘",
        Core.Cloud.CloudPlatforms.UC => "UC 网盘",
        Core.Cloud.CloudPlatforms.Baidu => "百度网盘",
        Core.Cloud.CloudPlatforms.Xunlei => "迅雷云盘",
        Core.Cloud.CloudPlatforms.C139 => "139 云盘",
        Core.Cloud.CloudPlatforms.Pan123 => "123 云盘",
        Core.Cloud.CloudPlatforms.Lanzou => "蓝奏云",
        Core.Cloud.CloudPlatforms.Pan115 => "115 网盘",
        Core.Cloud.CloudPlatforms.GuangYa => "光鸭云盘",
        _ => "网盘分享",
    };

    /// <summary>进入网盘子目录。</summary>
    private async Task EnterCloudDirAsync(AssetItemViewModel? item)
    {
        if (item == null || !item.IsDirectory) return;
        if (_cloudCtx == null || _services == null) return;

        try
        {
            // 先入栈再浏览：浏览失败时把栈退回去，避免「返回上级」指向错误位置
            _cloudDirStack.Push(_lastCloud?.CurrentDirectoryId);

            await LogAsync($"展开目录：{item.Name}…");
            var res = await _services.Sources
                .BrowseCloudAsync(_cloudCtx.Drive, _cloudCtx.Share, item.Locator, null)
                .ConfigureAwait(true);

            ApplyCloud(res, _resolvedFor ?? InputText.Trim());
            await LogAsync($"已进入：{item.Name} · {res.Note}");
        }
        catch (Exception e)
        {
            if (_cloudDirStack.Count > 0) _cloudDirStack.Pop();
            OnPropertyChanged(nameof(CanCloudBack));
            await LogAsync($"展开目录失败：{e.Message}", true);
        }
    }

    /// <summary>返回上一级目录。</summary>
    private async Task CloudBackAsync()
    {
        if (_cloudCtx == null || _services == null || _cloudDirStack.Count == 0) return;

        var parent = _cloudDirStack.Pop();
        try
        {
            var res = await _services.Sources
                .BrowseCloudAsync(_cloudCtx.Drive, _cloudCtx.Share, parent, null)
                .ConfigureAwait(true);
            ApplyCloud(res, _resolvedFor ?? InputText.Trim());
            await LogAsync($"返回上级：{res.Title}");
        }
        catch (Exception e)
        {
            _cloudDirStack.Push(parent);
            OnPropertyChanged(nameof(CanCloudBack));
            await LogAsync($"返回上级失败：{e.Message}", true);
        }
    }

    /// <summary>
    /// 下载网盘条目：先换直链，再入队。
    ///
    /// <para>为什么必须逐个取链而不是批量：各平台的取链接口一次只接受一个 fileId，
    /// 而且直链有有效期（夸克约 3 小时、115 的 CDN Cookie 只有 900 秒），
    /// 批量取完再排队会导致后几个任务拿到的是已过期地址。</para>
    /// </summary>
    private async Task DownloadCloudAsync()
    {
        if (_cloudCtx == null || _services == null) return;

        var picked = Assets.Where(a => a.IsSelected && !a.IsDirectory).ToList();
        if (picked.Count == 0)
        {
            bool hasDir = Assets.Any(a => a.IsDirectory);
            await LogAsync(hasDir ? "请先进入目录，勾选具体文件" : "没有勾选任何文件", true);
            return;
        }

        // 先切页：取链可能比较慢，用户应该立刻看到任务区。
        // 尊重「自动切到下载页」开关（原本这个设置从未被读取，属于死设置）。
        if (_settings.AutoSwitchToDownloads) Page = AppPage.Downloads;

        int queued = 0;
        foreach (var a in picked)
        {
            try
            {
                await LogAsync($"获取直链：{a.Name}…");

                var item = new ResolvedItem
                {
                    Name = a.Name,
                    Locator = a.Locator,
                    Size = a.Size,
                    Token = a.Token,
                };
                var link = await _services.Sources.GetCloudLinkAsync(_cloudCtx, item)
                    .ConfigureAwait(true);

                // 平台给的文件名更准（蓝奏会纠正扩展名、夸克带原后缀）
                string name = string.IsNullOrWhiteSpace(link.FileName) ? a.Name : link.FileName;
                name = FileNamePolicy.SanitizeFileName(name);

                Enqueue(link.Url, name, link.Headers, _cloudCtx);
                queued++;
            }
            catch (Exception e)
            {
                await LogAsync($"获取直链失败：{a.Name} — {e.Message}", true);
            }
        }

        if (queued > 0) await LogAsync($"已加入队列：{queued} 个文件");
    }

    // ===================== 下载 =====================

    private bool CanDownload() => HasParsed;

    private async Task DownloadAsync()
    {
        if (!HasParsed) return;

        // 磁力走 Gopeed 引擎：BT 不是 HTTP 分片，不能交给内置 DownloadEngine
        if (IsMagnetSource)
        {
            await DownloadMagnetAsync();
            return;
        }

        // 网盘走自己的链路：需要先换直链，并带上平台要求的 Referer/Cookie/UA
        if (IsCloudSource)
        {
            await DownloadCloudAsync();
            return;
        }

        // 多选资产时只下载勾选的
        List<(string Url, string Name)> items;
        if (ShowAssets && Assets.Count > 0)
        {
            items = Assets.Where(a => a.IsSelected).Select(a => (a.Url, a.Name)).ToList();
            if (items.Count == 0)
            {
                await LogAsync("没有勾选任何文件", true);
                return;
            }
        }
        else
        {
            items = Assets.Select(a => (a.Url, a.Name)).ToList();
        }

        if (items.Count == 0)
        {
            // 单项下载：直接用解析缓存里的 URL。
            // 早期实现在这里重新解析一次，等价于白调一次 API，且第二次失败会连带下载失败。
            if (_lastResolved == null)
            {
                await LogAsync("没有可下载的解析结果，请先点「解析」", true);
                return;
            }
            items = _lastResolved.Items.Select(i => (i.Url, i.FileName)).ToList();
        }

        foreach (var (url, name) in items)
            Enqueue(url, name);

        // 跳转到任务页：点了下载却停在原地看着"已加入队列"是糟糕的反馈
        if (_settings.AutoSwitchToDownloads) Page = AppPage.Downloads;

        await LogAsync($"已加入队列：{items.Count} 个文件");
    }

    private void Enqueue(string url, string name) => Enqueue(url, name, null, null);

    /// <param name="headers">
    /// 下载该链接必须携带的请求头。网盘直链普遍校验 Referer / Cookie / UA，缺一个就 403；
    /// GitHub 直链不需要，传 null 即可。
    /// </param>
    /// <param name="cloud">
    /// 该链接所属的网盘分享。非空时任务结束后会按分享做清理（百度/迅雷的转存文件）。
    /// </param>
    private void Enqueue(string url, string name, Dictionary<string, string>? headers,
        CloudShareContext? cloud = null)
    {
        var vm = new DownloadTaskViewModel
        {
            TaskId = _nextTaskId++,
            Url = url,
            FileName = name,
            State = TaskState.Queued,
            RequestHeaders = headers,
            CloudContext = cloud,
            // 记住入队时的下载目录：用户中途改设置后，续传/恢复仍要回原目录找断点
            SaveDir = _settings.DownloadDir,
            Platform = cloud?.Share.Platform ?? (url.Contains("github", StringComparison.OrdinalIgnoreCase)
                ? "github" : null),
        };
        Tasks.Add(vm);
        HasTasks = true;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DownloadTaskViewModel.Percent)
                or nameof(DownloadTaskViewModel.SpeedText))
                RecalcTotals();
        };

        if (cloud != null && vm.TryHoldShareRef()) AddShareRef(cloud);

        // ★ 入队即落盘。
        //
        //   早期只在"任务结束"（暂停/取消/失败）时才写库，于是下载中被强杀或断电时，
        //   这个任务在库里根本不存在 —— 重启后列表空的，断点也找不到，
        //   "断点续传"这个核心能力在最需要它的场景（异常退出）恰好失效。
        //   现在入队就先 INSERT 一行（state=Queued），后续按 3 秒节流 UPDATE 进度。
        PersistTask(vm, force: true);

        var task = RunTaskAsync(vm);
        lock (_running) _running.Add(task);
        task.ContinueWith(t =>
        {
            lock (_running) _running.Remove(t);
        }, TaskScheduler.Default);
        RecalcTotals();
    }

    // ===================== 网盘分享的转存清理 =====================

    /// <summary>
    /// 每个分享当前还有多少个任务在跑。
    ///
    /// <para><b>为什么要计数而不是每个任务结束就清</b>：百度把整个分享转存到
    /// <c>/YunX-Temp</c> 下（一个分享一个临时目录）。用户一次勾选 5 个文件，
    /// 如果第 1 个下完就把目录删了，剩下 4 个的直链立刻失效。</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _shareRefs = new();

    /// <summary>来自下载任务的分享上下文（清理时要用它的 Drive 与 Share）。</summary>
    private readonly ConcurrentDictionary<string, CloudShareContext> _shareContexts = new();

    private static string ShareKey(CloudShareContext ctx) =>
        $"{ctx.Drive.Platform}|{ctx.Share.ShareId}";

    private void AddShareRef(CloudShareContext ctx)
    {
        string key = ShareKey(ctx);
        _shareContexts[key] = ctx;
        _shareRefs.AddOrUpdate(key, 1, (_, v) => v + 1);
    }

    /// <summary>
    /// 任务结束时递减引用；归零则清理该分享的转存临时文件。
    ///
    /// <para>清理放在后台线程执行，失败只记日志 —— 清理是「善后」，绝不能影响
    /// 下载结果的状态流转（这一步在 <c>RunTaskAsync</c> 的 finally 里）。</para>
    /// </summary>
    private void ReleaseShareRef(DownloadTaskViewModel vm)
    {
        if (vm.CloudContext == null) return;

        // 只有真正持有过才递减：暂停后「继续」会重新持有，避免重复递减导致提前清理
        if (!vm.TryDropShareRef()) return;

        string key = ShareKey(vm.CloudContext);

        int left = _shareRefs.AddOrUpdate(key, 0, (_, v) => Math.Max(0, v - 1));
        if (left > 0) return;

        // 归零：原子摘除，避免两个任务同时归零时清理两次
        if (!_shareRefs.TryRemove(key, out _)) return;
        if (!_shareContexts.TryRemove(key, out var ctx)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await ctx.Drive.CleanupAsync(ctx.Share).ConfigureAwait(false);
                await LogAsync($"已清理网盘临时文件：{ctx.Drive.DisplayName} · {ctx.Share.ShareId}");
            }
            catch (Exception e)
            {
                await LogAsync($"清理网盘临时文件失败（不影响已下载文件）：{e.Message}", true);
            }
        });
    }

    /// <summary>
    /// 跑一个下载任务。
    ///
    /// <para><b>全程回到 UI 线程</b>：这里<b>故意不用</b> <c>ConfigureAwait(false)</c>。
    /// 原因有两个，都是实测会出问题的：</para>
    /// <list type="number">
    ///   <item><c>vm.State</c> / <c>vm.Done</c> 等属性都参与数据绑定，赋值必须在 UI 线程；
    ///         在线程池线程赋值会通过 <c>PropertyChanged</c> 触发视图更新 → WPF 抛
    ///         <c>InvalidOperationException</c>（跨线程访问）。</item>
    ///   <item>结尾的 <c>RecalcTotals()</c> 要枚举 <c>Tasks</c>（ObservableCollection）。
    ///         早期版本在 <c>finally</c> 里直接调它，而前置的 <c>ConfigureAwait(false)</c>
    ///         已经把执行流扔到线程池 —— 只要有 2 个以上任务排队（默认并发是 1，必然发生），
    ///         第二个任务结束时就会在线程池线程上枚举集合，用户同时删任务即抛
    ///         <c>Collection was modified</c>，异常又被 <c>UnobservedTaskException</c> 吞掉，
    ///         表现出来就是「状态栏数字不动了」。</item>
    /// </list>
    ///
    /// <para>真正的 IO 在 <see cref="DownloadEngine"/> 内部，那里有各自的
    /// <c>ConfigureAwait(false)</c>，所以这里回 UI 线程不会阻塞界面。</para>
    /// </summary>
    private async Task RunTaskAsync(DownloadTaskViewModel vm)
    {
        await AcquireSlotAsync().ConfigureAwait(true);
        try
        {
            // ★ 是否该停，看**任务状态**，不看 CTs。
            //
            //   早期这里是 `if (vm.Cts?.IsCancellationRequested == true) return;`，
            //   而 PauseTask 只 Cancel 不置空 —— 于是：
            //   · 暂停后点「继续」→ 旧的已取消 CTS 还在 → 这里立刻 return →
            //     任务停在 Queued，而 CanResume 只接受 Paused/Failed → 用户再也点不了继续。
            //   · 排队中取消/删除 → 那时 Cts 还是 null → 判断直接放行 →
            //     一个界面上已不存在的任务在后台偷偷开始下载。
            //
            //   改成显式状态判断：只有「已被取消」或「已被移除」才放弃执行。
            //   暂停不在其列 —— 暂停后重新入队必须能真正跑起来。
            if (vm.Cancelled || vm.RemoveRequested) return;

            // 每轮执行都用全新的 CTS。取消后不复用（CTS 一旦取消就永久失效）。
            vm.Cts?.Dispose();
            vm.Cts = new CancellationTokenSource();
            var ct = vm.Cts.Token;
            vm.State = TaskState.Running;
            vm.Phase = DownloadPhase.Probing;

            // ★ 时间统计的三个锚点。
            //
            //   TransferStartedAt 只在**第一次**开始时记（续传不改），
            //   这样「开始时间」始终是首次真正下载的那一刻，语义稳定。
            //   RunStartedAt 每轮都刷新，用来算本轮耗时累加到 Elapsed。
            vm.TransferStartedAt ??= DateTime.Now;
            vm.RunStartedAt = DateTime.Now;
            // 重新开始跑 = 还没结束，清掉上一次的完成时刻
            vm.FinishedAt = null;

            // Progress 必须在**任何 await 之前**创建：
            // 它靠捕获构造时的 SynchronizationContext 来决定回调投递到哪个线程。
            // 晚于 await 创建时 Current 可能已是 null，回调就落到线程池了。
            var progress = new Progress<DownloadProgress>(p =>
            {
                // ★ TotalBytes == 0 是「纯阶段通知」（探测阶段还没拿到文件大小）。
                //   这类回调只更新文案，绝不能碰 Done/Size —— 否则会把续传的既有进度
                //   清成 0，用户看到进度条忽然归零。
                if (p.TotalBytes > 0)
                {
                    vm.Done = p.BytesDone;
                    vm.Size = p.TotalBytes;
                }
                vm.Speed = p.SpeedBytesPerSec;
                vm.Phase = p.Phase;

                // 进度落盘（节流：force=false 时按 3 秒一次）。
                // 目的是"强杀/断电后重启能知道下到哪了"；断点的真实位置存在
                // PartMetadata 里，这里记的是给用户看的进度。
                if (p.TotalBytes > 0) PersistTask(vm, force: false);
            });

            var engine = new DownloadEngine(_provider, _pool);
            vm.Engine = engine;

            var options = new DownloadOptions
            {
                // 按平台覆盖线程数（网盘接口限流更严，GitHub 可以开高一些）。
                // 这个设置项原本存在但 ThreadsFor() 无人调用 —— 改了不起作用。
                Threads = ResolveThreads(vm),
                Resume = _settings.Resume,
                SpeedLimitBytesPerSec = _settings.SpeedLimitKb > 0
                    ? (long)_settings.SpeedLimitKb * 1024 : 0,
                PreemptSlowChunks = _settings.PreemptSlowChunks,
                CacheDir = string.IsNullOrWhiteSpace(_settings.CacheDir) ? null : _settings.CacheDir,
                VerifyChunks = _settings.VerifyChunks,
                // 完成后整文件 SHA-256。原本这个开关只有引擎支持、Desktop 从不赋值，
                // 等于永远关着（矩阵里却写了 ✅）。
                VerifyHash = _settings.VerifyHash,
                Platform = vm.Url.Contains("github", StringComparison.OrdinalIgnoreCase)
                    ? "github" : "cloud",
                Headers = vm.RequestHeaders ?? new Dictionary<string, string>(),
                Tuning = new DownloadTuning
                {
                    ReadBufferBytes = Math48.Clamp(_settings.ReadBufferKb, 4, 4096) * 1024,
                    PreemptFloorBps = Math48.Clamp(_settings.PreemptFloorKb, 1, 10240) * 1024.0,
                    PreemptMinAgeMs = Math48.Clamp(_settings.PreemptAgeSec, 1, 600) * 1000.0,
                },
                // 记下断点落点（可能在缓存目录）：删除任务时要靠它回收孤儿 part
                OnPartPathResolved = p => vm.PartPath = p,
                // 同名文件已存在时引擎会改名（name (1).ext），界面必须跟着改，
                // 否则两个任务显示同一个名字，用户分不清哪个是这次下的。
                OnFileNameResolved = name =>
                {
                    // ★ 必须同步落盘，不能只 Post 一下就返回。
                    //
                    //   改名发生在下载「探测完、开始写数据之前」。用 Post 时这行只是
                    //   排进消息队列就返回了，而下载可能很快就跑完并执行
                    //   PersistTask(Done) —— 两个写库操作谁先谁后不确定。
                    //   Send 会等 UI 线程执行完再返回，顺序才有保证。
                    //
                    //   ★ 但要防死锁：Send 在 UI 线程自身调用会永久阻塞。
                    //   引擎在 Task.Run 里跑，正常不会是 UI 线程；仍然判一次更安全。
                    void Apply()
                    {
                        vm.FileName = name;
                        // 改名必须立刻落盘：否则界面是新名字、库里是旧名字，
                        // 重启后又变回去，而磁盘上的文件是按新名字存的，两边对不上。
                        PersistTask(vm, force: true);
                    }

                    if (SynchronizationContext.Current == _ui) Apply();
                    else _ui.Send(_ => Apply(), null);
                },
                // 目标目录已有同名文件：问用户要自定义名称还是覆盖。
                //
                // 引擎在后台线程调用它，而 Choose/Ask 是模态窗口，必须回到 UI 线程。
                // 用 Send（同步等待）而不是 Post：这里需要拿到用户的选择才能继续。
                ResolveNameConflict = conflict =>
                {
                    // 默认自动改名：万一调度失败，也绝不动用户已有文件
                    var decision = NameConflictDecision.AutoRename;

                    _ui.Send(_ =>
                    {
                        int pick = Views.PromptDialog.ChooseWithPrimary(
                            "同名文件已存在",
                            $"下载目录里已经有「{conflict.FileName}」" +
                            $"（{YunX.Core.Util.Humanize.Size(conflict.ExistingSize)}）。\n\n" +
                            "要怎么做？", 1,
                            "覆盖原文件", "重命名文件");

                        if (pick < 0) { decision = NameConflictDecision.Cancel; return; }

                        if (pick == 0)
                        {
                            // 覆盖是破坏性操作：删的是用户已有文件，必须二次确认
                            int sure = Views.PromptDialog.Choose(
                                "确认覆盖",
                                $"即将删除已有文件并重新下载：\n{conflict.FileName}\n\n" +
                                "原文件内容将丢失，且无法恢复。确定继续吗？",
                                "覆盖");
                            decision = sure == 0
                                ? NameConflictDecision.Overwrite
                                : NameConflictDecision.Cancel;
                            return;
                        }

                        // 重命名：预填当前名字，用户直接改成想要的名字
                        string? name = Views.PromptDialog.Ask(
                            "重命名文件",
                            "输入新的文件名（留空则用系统自动生成的名字）：",
                            initial: conflict.FileName);

                        if (name == null) { decision = NameConflictDecision.Cancel; return; }

                        name = name.Trim();
                        decision = name.Length > 0
                            ? NameConflictDecision.Rename(name)
                            : NameConflictDecision.AutoRename;
                    }, null);

                    return decision;
                },
            };

            try
            {
                var result = await engine.DownloadAsync(
                    vm.Url, _settings.DownloadDir, vm.FileName, options,
                    progress, m =>
                    {
                        // 引擎的每一行日志都记进该任务的调试事件流，
                        // 这样调试页能按任务看到「测速 → 分片规划 → 换连接 → 完成」的完整时序
                        _services?.DownloadDebug.Record(vm.TaskId, "engine", m);
                        _ = LogAsync(m);
                    }, ct).ConfigureAwait(true);

                vm.ResultPath = result.FilePath;
                vm.Done = result.Size;
                vm.Size = result.Size;
                vm.Speed = 0;
                vm.State = TaskState.Done;
                vm.Phase = DownloadPhase.Completed;

                await LogAsync(
                    $"完成：{Path.GetFileName(result.FilePath)} · {Humanize.Size(result.Size)} · " +
                    $"{result.ElapsedSeconds:0.0}s · 平均 {Humanize.Speed(result.AverageSpeedBps)} · " +
                    $"线路 {result.MirrorName}" +
                    (result.Preemptions > 0 ? $" · 换连接 {result.Preemptions} 次" : ""));

                if (_settings.OpenFolderWhenDone)
                    OpenInExplorer(Path.GetDirectoryName(result.FilePath));

                // 通知由窗口决定弹不弹（窗口活跃时不打扰）
                if (_settings.NotifyOnComplete)
                {
                    try { TaskCompleted?.Invoke(Path.GetFileName(result.FilePath),
                        Humanize.Size(result.Size)); }
                    catch { /* 通知失败不影响下载结果 */ }
                }

                // ★ 完成不再是"删掉记录"，而是**转为历史**（state=Done）。
                //
                //   早期这里是 DELETE，理由是"完成即交付，恢复没有意义"。道理没错，
                //   但那同时也把"下载过什么"一并抹掉了 —— 用户重启后看到空列表，
                //   分不清是自己没下过、还是程序把记录弄丢了。
                //
                //   现在：落盘为 Done，重启时由 LoadCompleted 读回来显示，但不参与恢复。
                //   条数由 PruneCompleted 收敛到「最近 N 条」。
                // 耗时优先用引擎的统计：它只累计真正在传数据的时间，
                // 比「墙钟相减」准确（墙钟会把等待、重连都算进去）。
                vm.Elapsed += result.ElapsedSeconds > 0
                    ? TimeSpan.FromSeconds(result.ElapsedSeconds)
                    : WallSinceRunStart(vm);
                vm.FinishedAt = DateTime.Now;

                PersistTask(vm, force: true);
                PruneCompletedHistory();
            }
            catch (DownloadCancelledException)
            {
                // 「暂停」也是走取消通道停下来的 —— 引擎只认 CancellationToken，
                // 区分暂停与放弃是上层的事（PauseRequested / 之后能否续传）。
                //
                // 早期实现无条件写 Cancelled，而 PauseTask 已经先把状态设成了 Paused，
                // 于是用户点暂停后会被这一行覆盖成「已取消」；而 ResumeTask 只接受
                // Paused / Failed，「继续」按钮直接变灰 —— 暂停实际上不可逆。
                if (vm.PauseRequested)
                {
                    // 消费掉标志：不然下一次真正「取消」会被误判成暂停
                    vm.PauseRequested = false;
                    vm.State = TaskState.Paused;
                    vm.Speed = 0;
                    // 暂停：把本轮已跑的时间记进累计，不清空（之后继续还会往上加）
                    vm.Elapsed += WallSinceRunStart(vm);
                    await LogAsync($"已暂停：{vm.FileName}（断点已保留）");
                    PersistTask(vm);
                }
                else
                {
                    vm.State = TaskState.Cancelled;
                    vm.Speed = 0;
                    vm.Elapsed += WallSinceRunStart(vm);
                    // 取消是「这一次」的终点 —— 记下时刻，但断点仍在，之后继续会清掉它
                    vm.FinishedAt = DateTime.Now;
                    await LogAsync($"已取消：{vm.FileName}（断点已保留）", true);
                    // 取消也要落盘：否则重启后这个任务的断点无人知晓，
                    // 用户以为"取消可续传"却发现列表里没了。
                    PersistTask(vm);
                }
            }
            catch (Exception e)
            {
                vm.State = TaskState.Failed;
                vm.Error = e.Message;
                vm.Speed = 0;
                vm.Elapsed += WallSinceRunStart(vm);
                vm.FinishedAt = DateTime.Now;
                await LogAsync($"失败：{vm.FileName} — {e.Message}", true);
                // ★ 不能只写日志。用户在别的页面、或者没看日志面板时，
                //   任务悄悄变成"失败"他完全不知道，只会觉得"下着下着不见了"。
                //   浮层不打断操作，批量失败也不会连弹模态框。
                RaiseAlert($"下载失败：{vm.FileName}", DescribeDownloadError(e.Message));
                // 失败最需要落盘：它保留了可续传的断点，重启后应能重试
                PersistTask(vm);
            }
        }
        finally
        {
            ReleaseSlot();

            // ★ 暂停 / 取消都**不能**触发网盘转存清理。
            //
            //   百度/迅雷必须先「转存到自己网盘」才给直链，用完要删。但暂停和取消
            //   都走取消通道，也会走到这里：引用归零 → 立刻清理转存目录 →
            //   用户点「继续」时直链指向已被删除的资源，续传必然失败。
            //
            //   只有「真的不要了」才清理：完成（Done）、删除任务、程序退出。
            //   Cancelled 状态是"暂时放弃但可续传"，必须保住转存。
            bool keepShareRef = vm.State is TaskState.Paused or TaskState.Cancelled;
            if (!keepShareRef)
            {
                try { ReleaseShareRef(vm); } catch { /* 善后失败不影响任务状态 */ }
            }
            RecalcTotals();
        }
    }

    // ===================== 任务并发槽位 =====================

    private readonly object _slotGate = new();
    private int _runningTasks;

    /// <summary>
    /// 取得一个并发槽位。
    ///
    /// <para><b>为什么不用固定容量的 <see cref="SemaphoreSlim"/></b>：上限是用户在设置里
    /// 随时可改的。早期实现的做法是「重建信号量 + Dispose 旧的」，但
    /// <c>SemaphoreSlim.Dispose()</c> <b>不会释放正在 <c>WaitAsync</c> 的等待者</b> ——
    /// 用户改一次并发数，所有排队中的任务就永久卡在「等待中」再也醒不过来；
    /// 而且旧实例的 <c>Release()</c> 落到新实例上，限额被静默突破。</para>
    ///
    /// <para>改为「计数 + 短轮询」：上限改动立即生效，不持有任何可被 Dispose 的等待原语。
    /// 120ms 的轮询延迟对秒级以上的下载任务毫无影响，换来的是不会卡死。</para>
    /// </summary>
    private async Task AcquireSlotAsync()
    {
        while (true)
        {
            lock (_slotGate)
            {
                int limit = Math.Max(1, _settings.MaxConcurrentTasks);
                if (_runningTasks < limit)
                {
                    _runningTasks++;
                    return;
                }
            }

            await Task.Delay(120).ConfigureAwait(true);
        }
    }

    private void ReleaseSlot()
    {
        lock (_slotGate)
        {
            if (_runningTasks > 0) _runningTasks--;
        }
    }

    // ===================== 交互 =====================

    /// <summary>
    /// 一键清空输入框。
    ///
    /// <para><b>为什么替换掉了「粘贴」</b>：输入框本身就能 <c>Ctrl+V</c>，
    /// 粘贴按钮只在「鼠标操作」时省一次右键，收益很小；而清空是刚需 ——
    /// 粘错链接、或者在同一个窗口反复解析不同链接时，都要先全选删除。
    /// 清空后顺手聚焦回输入框，可以直接粘下一条。</para>
    /// </summary>
    private void ClearInput()
    {
        InputText = "";
        ClearInputRequested?.Invoke();
    }

    /// <summary>UI 层订阅：把输入框重新聚焦（ViewModel 不直接持有控件）。</summary>
    public event Action? ClearInputRequested;

    private void BrowseCacheDirectory()
    {
#if NET48
        // net48 没有 Microsoft.Win32.OpenFolderDialog（.NET 8 新增），
        // 用 Compat/FolderDialog.cs 里的 IFileOpenDialog 实现（界面一致）。
        var dlg = new YunX.Desktop.Compat.FolderDialog
        {
            Title = "选择分片缓存目录（建议放 SSD）",
        };
        if (dlg.ShowDialog()) CacheDir = dlg.FolderName;
#else
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择分片缓存目录（建议放 SSD）",
        };
        if (dlg.ShowDialog() == true) CacheDir = dlg.FolderName;
#endif
    }

    private void BrowseDirectory()
    {
#if NET48
        var dlg = new YunX.Desktop.Compat.FolderDialog
        {
            Title = "选择下载目录",
            InitialDirectory = Directory.Exists(DownloadDir) ? DownloadDir : "",
        };
        if (dlg.ShowDialog())
#else
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择下载目录",
            InitialDirectory = Directory.Exists(DownloadDir) ? DownloadDir : "",
        };
        if (dlg.ShowDialog() == true)
#endif
        {
            DownloadDir = dlg.FolderName;
            _settings.DownloadDir = dlg.FolderName;
            _store.Save(_settings);
        }
    }

    private void OpenDownloadDir() => OpenInExplorer(_settings.DownloadDir);

    private void OpenTaskFolder(object? param)
    {
        if (param is DownloadTaskViewModel vm && vm.ResultPath is { Length: > 0 } path)
            OpenInExplorer(Path.GetDirectoryName(path));
        else
            OpenInExplorer(_settings.DownloadDir);
    }

    private static void OpenInExplorer(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打开目录失败无需打扰用户
        }
    }

    /// <summary>切换分区。参数为分区名字符串（与按钮 Tag 对应）。</summary>
    private void Navigate(object? param)
    {
        string key = (param as string ?? "").ToLowerInvariant();
        Page = key switch
        {
            "downloads" => AppPage.Downloads,
            "accounts" => AppPage.Accounts,
            "debug" => AppPage.Debug,
            "settings" => AppPage.Settings,
            "favorites" => AppPage.Favorites,
            _ => AppPage.Parse,
        };
    }



    // ================= 剪贴板链接检测 =================

    /// <summary>
    /// 启动剪贴板轮询。
    ///
    /// <para><b>为什么用轮询而不是剪贴板监听链</b>：<c>AddClipboardFormatListener</c> 需要
    /// 自定义窗口句柄消息钩子（<c>HwndSourceHook</c> 或 <c>WM_CLIPBOARDUPDATE</c> 处理），
    /// 换来的只是「即时触发」而非 1 秒内的差别；轮询实现只有十几行且不会被窗口重创建搞坏。</para>
    ///
    /// <para>1.2 秒的间隔是权衡：更短会让频繁复制大文本时反复读剪贴板，
    /// 更长则用户会觉得「我复制了怎么没反应」。</para>
    /// </summary>
    private void StartClipboardWatch()
    {
        if (_clipTimer == null)
        {
            _clipTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200),
            };
            _clipTimer.Tick += (_, _) => PollClipboard();
        }

        if (_clipTimer.IsEnabled) return;

        // 每次重新开启都重记一次当前内容，避免「关掉期间复制的东西」在打开时立刻弹提示
        try
        {
            if (Clipboard.ContainsText())
                _clipSeenHash = Hash(Clipboard.GetText());
        }
        catch
        {
            // 剪贴板被别的进程占用，忽略
        }

        _clipTimer.Start();
    }

    /// <summary>停掉剪贴板轮询。关掉开关后就该真正停下来，否则 Timer 会一直把 VM 钉在 Dispatcher 上。</summary>
    private void StopClipboardWatch()
    {
        _clipTimer?.Stop();
    }

    /// <summary>按设置让轮询与实际状态一致。</summary>
    private void ApplyClipboardWatch()
    {
        if (_settings.ClipboardWatch) StartClipboardWatch();
        else StopClipboardWatch();
    }

    private void PollClipboard()
    {
        if (!_settings.ClipboardWatch) return;

        string text;
        try
        {
            if (!Clipboard.ContainsText()) return;
            text = Clipboard.GetText();
        }
        catch
        {
            // 剪贴板正被别的程序写入（COM 占用），下一轮再试
            return;
        }

        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000) return;

        string hash = Hash(text);
        if (hash == _clipSeenHash) return;
        _clipSeenHash = hash;

        // 只认「本程序能处理的输入」：网盘链接由各适配器的识别规则判断（纯本地，不发网络），
        // GitHub 侧用域名关键字粗筛 —— 精确解析要发请求，不能在轮询里做。
        bool looksInteresting = false;
        try
        {
            looksInteresting = _services?.CloudDrives.Detect(text) != null ||
                               text.Contains("github.com", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 识别失败就当不是链接
        }

        if (!looksInteresting) return;

        _clipPending = text.Trim();
        ClipboardHintText = Preview(_clipPending);
        ClipboardHintVisible = true;
        ClipboardHintShown?.Invoke();
        _ = LogAsync("剪贴板检测到链接（点击提示卡片可填入）");
    }

    /// <summary>把链接填进输入框并收起提示。</summary>
    private void UseClipboardHint(object? param)
    {
        if (_clipPending.Length == 0) return;
        InputText = _clipPending;
        DismissClipboardHint(null);
        _ = LogAsync("已从剪贴板填入链接");
    }

    private void DismissClipboardHint(object? param)
    {
        ClipboardHintVisible = false;
        _clipPending = "";
    }

    /// <summary>提示卡片上显示的短预览：保留头尾，中间省略。</summary>
    private static string Preview(string text)
    {
        string one = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return one.Length <= 64 ? one : one[..44] + "…" + one[^16..];
    }

    private static string Hash(string text)
    {
        var bytes = Hash48.Sha256(Encoding.UTF8.GetBytes(text));
        return Helpers.ToHexString(bytes, 0, 8);
    }

    // ================= 任务持久化 =================

    /// <summary>持久化失败最多只记这么多次，避免刷屏。</summary>
    private int _persistFailures;

    /// <summary>
    /// 进度写盘的节流窗口。
    ///
    /// <para><b>为什么要节流</b>：进度回调在下载热路径上，每秒可能触发几十次。
    /// SQLite 即使走 WAL，每次 UPDATE 也要落一次事务日志 —— 100KB/s 的小文件还好，
    /// 但 8 线程满速下载时实测磁盘写入会被拖成瓶颈，而且对 SSD 是无谓的磨损。</para>
    ///
    /// <para><b>为什么取 5 秒</b>：强杀场景下最多丢失 5 秒进度。
    /// 断点续传本身的粒度是「整块分片」，丢 5 秒的字节计数不影响续传正确性 ——
    /// 重启后会从断点元数据里恢复真实位置，这个数值只是用于显示。
    /// 状态变化（暂停/完成/失败）不走节流，立即写，所以这个窗口只影响"进度数字"。</para>
    /// </summary>
    private static readonly TimeSpan ProgressWriteInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 把已完成记录裁剪到「最近 N 条」。
    ///
    /// <para>在每次任务完成时调用，而不是只在启动时 —— 一次会话里连下 200 个文件时，
    /// 表不会先涨到 200 条再被砍。</para>
    /// </summary>
    private void PruneCompletedHistory()
    {
        if (_services == null) return;
        try
        {
            _services.Tasks.PruneCompleted(Math.Max(1, _settings.CompletedHistoryLimit));
        }
        catch (Exception e)
        {
            // 裁剪失败不影响下载结果，但要说出来 —— 否则库会无声地一直涨
            _ = LogAsync($"清理历史记录失败：{e.Message}", true);
        }
    }

    /// <summary>
    /// 把任务写入数据库（用于重启恢复 + 下载历史）。
    ///
    /// <para><b>写什么</b>：
    /// <list type="bullet">
    /// <item>未完成（排队/运行/暂停/取消/失败）→ 重启后恢复断点；</item>
    /// <item>已完成 → 只作为历史保留，不参与恢复，数量由 <see cref="CompletedHistoryLimit"/> 限制。</item>
    /// </list></para>
    ///
    /// <para><b>Id 的语义</b>：<see cref="DownloadTaskViewModel.DbId"/> 为 0 表示还没写过，
    /// 走 INSERT 并由仓储回写真实主键；之后非 0，走 UPDATE。<b>不能</b>再拿
    /// <see cref="DownloadTaskViewModel.TaskId"/>（进程内编号）当主键 ——
    /// 那会让首次写入也走 UPDATE，而表里没有对应行。</para>
    ///
    /// <paramref name="force"/> 为 false 时按 <see cref="ProgressWriteInterval"/> 节流，
    /// 只对「运行中」的任务生效 —— 其它状态都是离散事件，一律立即写。
    /// </summary>
    private void PersistTask(DownloadTaskViewModel vm, bool force = true)
    {
        if (_services == null) return;

        // 运行中的进度写盘要节流；若距上次写盘不足间隔，直接跳过（下次会补上）
        if (!force && vm.State == TaskState.Running)
        {
            var now = DateTime.UtcNow;
            if (now - vm.LastPersistAt < ProgressWriteInterval) return;
            vm.LastPersistAt = now;
        }

        try
        {
            var row = new PersistedTask
            {
                Id = vm.DbId,
                Url = vm.Url,
                FileName = vm.FileName,
                SaveDir = string.IsNullOrEmpty(vm.SaveDir) ? _settings.DownloadDir : vm.SaveDir,
                TotalBytes = vm.Size,
                DoneBytes = vm.Done,
                State = vm.State switch
                {
                    TaskState.Paused => PersistedTaskState.Paused,
                    TaskState.Running => PersistedTaskState.Running,
                    TaskState.Failed => PersistedTaskState.Failed,
                    TaskState.Done => PersistedTaskState.Done,
                    // 取消单独记，不要混进 Queued：重启恢复时两者的 UX 完全不同
                    TaskState.Cancelled => PersistedTaskState.Cancelled,
                    _ => PersistedTaskState.Queued,
                },
                Error = vm.Error,
                PayloadJson = BuildTaskPayload(vm),
                // 时间三件套：开始 / 完成 / 累计耗时。
                // 0 表示「还没有」，读取侧据此决定显示什么。
                TransferStartAt = ToUnix0(vm.TransferStartedAt),
                FinishedAt = ToUnix0(vm.FinishedAt),
                ElapsedSeconds = (long)Math.Round(vm.Elapsed.TotalSeconds),
            };

            _services.Tasks.Upsert(row);

            // 首次写入由仓储回写主键（last_insert_rowid），后续就都走 UPDATE 了
            if (vm.DbId == 0 && row.Id > 0) vm.DbId = row.Id;
        }
        catch (Exception e)
        {
            // ★ 不能静默吞掉。
            //   早期这里是空 catch，恰好掩盖了「$upd 参数未绑定」这个必然异常 ——
            //   于是持久化一次都没成功过，而界面上、日志里都毫无痕迹，
            //   只表现为「重启后什么都没有」，根本无从排查。
            //   现在记一次（限次防刷屏），并写进诊断日志。
            if (Interlocked.Increment(ref _persistFailures) <= 5)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PersistTask] 持久化失败（第 {_persistFailures} 次）：{e.GetType().Name}: {e.Message}");
                _ = LogAsync($"任务持久化失败：{e.Message}", true);
            }
        }
    }

    /// <summary>
    /// 把「无法放进独立列、但恢复时必须还原」的上下文序列化成 JSON。
    ///
    /// <para>包含：请求头（网盘直链硬校验 Referer/Cookie）、网盘分享上下文（续传要重新占引用、
    /// 结束要清理转存）、断点文件路径（删除任务时要回收孤儿 part）、下载目录快照
    /// （用户改过设置后仍应回到原来的位置续传）。</para>
    /// </summary>
    private static string BuildTaskPayload(DownloadTaskViewModel vm)
    {
        try
        {
            var payload = new TaskPayload
            {
                Headers = vm.RequestHeaders,
                PartPath = vm.PartPath,
                SaveDir = vm.SaveDir,
                Platform = vm.Platform,
                Cloud = vm.CloudContext == null ? null : new CloudPayload
                {
                    // Drive.Platform / Share.Platform 都是字符串标识（CloudPlatforms 里那套）
                    Platform = vm.CloudContext.Drive.Platform,
                    ShareId = vm.CloudContext.Share.ShareId,
                    Passcode = vm.CloudContext.Share.Passcode ?? "",
                    RawInput = vm.CloudContext.Share.RawInput,
                },
            };

            if (payload.Headers == null && payload.PartPath == null
                && payload.Cloud == null && payload.Platform == null)
                return "";

            return JsonSerializer.Serialize(payload);
        }
        catch
        {
            // 序列化失败不该影响下载；恢复时退化为"只有基本信息"
            return "";
        }
    }

    /// <summary>持久化到 <c>payload_json</c> 的附属信息。</summary>
    private sealed class TaskPayload
    {
        public Dictionary<string, string>? Headers { get; set; }
        public string? PartPath { get; set; }
        public string? SaveDir { get; set; }
        public string? Platform { get; set; }
        public CloudPayload? Cloud { get; set; }
    }

    private sealed class CloudPayload
    {
        public string Platform { get; set; } = "";
        public string ShareId { get; set; } = "";
        public string Passcode { get; set; } = "";
        public string RawInput { get; set; } = "";
    }

    /// <summary>
    /// 从数据库恢复上次未完成的任务（重启后进度不丢）。
    ///
    /// <para><b>状态分流</b>：早期实现把非 Paused 的行一律恢复为 Queued，
    /// 而 <see cref="ResumeTask"/> 只接受 Paused/Failed —— 恢复出来的任务卡在「等待中」，
    /// 既不会自动跑，也点不了继续，只能取消或删除。现在：</para>
    /// <list type="bullet">
    ///   <item>Paused → 保持 Paused，用户手动点继续（尊重他当初的暂停意图）；</item>
    ///   <item>Failed → 保持 Failed，可重试；</item>
    ///   <item>Queued/Running（上次是被强杀的）→ 恢复为 Paused 而不是 Queued：
    ///         重启后不擅自占满带宽开始下载，但用户点继续即可续传。</item>
    /// </list>
    /// </summary>
    private void RestorePersistedTasks()
    {
        if (_services == null) return;
        try
        {
            var list = _services.Tasks.LoadRestorable();
            foreach (var t in list)
            {
                var payload = ParseTaskPayload(t.PayloadJson);

                var vm = new DownloadTaskViewModel
                {
                    // TaskId 是进程内编号，恢复时重新分配；DbId 才是库里的主键
                    TaskId = _nextTaskId++,
                    DbId = t.Id,
                    Url = t.Url,
                    FileName = t.FileName,
                    Size = t.TotalBytes,
                    Done = t.DoneBytes,
                    Error = t.Error,
                    // ★ 还原 payload 里的上下文，否则「继续」等于换了个任务重下：
                    //   缺 Referer/Cookie → 网盘直链 403；缺 PartPath → 找不到断点；
                    //   缺 SaveDir → 用户改过设置就去错目录找。
                    RequestHeaders = payload?.Headers,
                    PartPath = payload?.PartPath,
                    SaveDir = payload?.SaveDir ?? t.SaveDir,
                    Platform = payload?.Platform,
                    // 时间信息一并还原，否则重启后「用时」全变成 0
                    TransferStartedAt = FromUnixOrNull(t.TransferStartAt),
                    FinishedAt = FromUnixOrNull(t.FinishedAt),
                    Elapsed = TimeSpan.FromSeconds(t.ElapsedSeconds),
                };

                vm.State = t.State switch
                {
                    PersistedTaskState.Paused => TaskState.Paused,
                    PersistedTaskState.Failed => TaskState.Failed,
                    // Queued / Running：上次没正常结束（强杀、断电）。恢复成 Paused 让用户决定何时继续。
                    _ => TaskState.Paused,
                };

                Tasks.Add(vm);
                _nextTaskId = Math.Max(_nextTaskId, (int)t.Id + 1);
            }

            if (list.Count > 0)
            {
                HasTasks = true;
                int resumable = Tasks.Count(x => x.CanResume);
                // 只说真话：Paused/Failed 才能点继续
                _ = LogAsync($"已恢复 {list.Count} 个未完成任务，其中 {resumable} 个可点继续");
            }

            RestoreCompletedHistory();
            RecalcTotals();
        }
        catch (Exception e)
        {
            _ = LogAsync($"恢复任务失败：{e.Message}", true);
        }
    }

    /// <summary>
    /// 把已完成的下载记录显示回列表（只作历史，不参与恢复）。
    ///
    /// <para><b>为什么单独一个方法</b>：这些行和"未完成任务"在库里的状态、在 UI 上的语义
    /// 都不同 —— 它们没有断点、不能续传，只能「打开所在文件夹」或删除。
    /// 混在 <see cref="RestorePersistedTasks"/> 里会让那段逻辑充满分支。</para>
    ///
    /// <para>历史按完成时间倒序插在未完成任务之后，保证「待办在上、历史在下」。</para>
    /// </summary>
    private void RestoreCompletedHistory()
    {
        if (_services == null) return;
        try
        {
            int limit = Math.Max(1, _settings.CompletedHistoryLimit);
            var done = _services.Tasks.LoadCompleted(limit);
            if (done.Count == 0) return;

            foreach (var t in done)
            {
                var payload = ParseTaskPayload(t.PayloadJson);

                var vm = new DownloadTaskViewModel
                {
                    TaskId = _nextTaskId++,
                    DbId = t.Id,
                    Url = t.Url,
                    FileName = t.FileName,
                    Size = t.TotalBytes > 0 ? t.TotalBytes : t.DoneBytes,
                    Done = t.TotalBytes > 0 ? t.TotalBytes : t.DoneBytes,
                    SaveDir = payload?.SaveDir ?? t.SaveDir,
                    Platform = payload?.Platform,
                    State = TaskState.Done,
                    Phase = DownloadPhase.Completed,
                    TransferStartedAt = FromUnixOrNull(t.TransferStartAt),
                    FinishedAt = FromUnixOrNull(t.FinishedAt),
                    Elapsed = TimeSpan.FromSeconds(t.ElapsedSeconds),
                };

                // 文件被用户删掉/移走后，历史仍然保留（那是"下过"的记录），
                // 但 UI 上"打开文件夹"会失败 —— 这里标记出来，让按钮能给出准确提示。
                try
                {
                    string full = Path.Combine(vm.SaveDir, vm.FileName);
                    if (File.Exists(full)) vm.ResultPath = full;
                }
                catch { /* 路径非法就当文件不存在 */ }

                Tasks.Add(vm);
                _nextTaskId = Math.Max(_nextTaskId, (int)t.Id + 1);
            }

            HasTasks = true;
            _ = LogAsync($"已载入 {done.Count} 条下载历史");
        }
        catch (Exception e)
        {
            // 历史加载失败不该拖累主流程（未完成任务已经恢复了）
            _ = LogAsync($"载入下载历史失败：{e.Message}", true);
        }
    }

    /// <summary>
    /// 本轮已运行的墙钟时长。用于引擎没给耗时（失败/取消）的分支。
    ///
    /// <para>用完把 <see cref="DownloadTaskViewModel.RunStartedAt"/> 置空，
    /// 避免同一轮被重复累计（例如暂停后又失败）。</para>
    /// </summary>
    /// <summary>
    /// 把下载失败的技术错误翻译成用户能行动的建议。
    ///
    /// <para>直接抛 `HttpRequestException` / `IOException` 给用户等于没说 ——
    /// 他既不知道发生了什么，也不知道该做什么。</para>
    /// </summary>
    private static string DescribeDownloadError(string raw)
    {
        if (raw.Contains("403", StringComparison.Ordinal))
            return "服务器拒绝了访问（403）。网盘直链可能已过期，请重新解析。";
        if (raw.Contains("404", StringComparison.Ordinal))
            return "文件不存在（404）。可能已被删除或改名，请重新解析。";
        if (raw.Contains("所有线路均不可用", StringComparison.Ordinal))
            return "所有线路都连不上。检查网络，或在设置里换一个下载线路。";
        if (raw.Contains("超时", StringComparison.Ordinal) ||
            raw.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "连接超时。网络不稳或线路拥塞，可以稍后点「继续」从断点续传。";
        if (raw.Contains("磁盘", StringComparison.Ordinal) ||
            raw.Contains("space", StringComparison.OrdinalIgnoreCase))
            return "磁盘写入失败。检查目标磁盘剩余空间和写权限。";
        if (raw.Contains("无法覆盖", StringComparison.Ordinal))
            return raw;   // 已经是人话，原样给
        return raw + "\n\n可点「继续」从断点重试；仍失败请换线路。";
    }

    /// <summary>本地时间 → Unix 秒；null 记 0，表示「没有这个时刻」。</summary>
    private static long ToUnix0(DateTime? t)
        => t is { } v ? new DateTimeOffset(v).ToUnixTimeSeconds() : 0;

    /// <summary>Unix 秒 → 本地时间；0 表示「没有这个时刻」。</summary>
    private static DateTime? FromUnixOrNull(long seconds)
        => seconds <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;

    private static TimeSpan WallSinceRunStart(DownloadTaskViewModel vm)
    {
        if (vm.RunStartedAt is not { } started) return TimeSpan.Zero;
        vm.RunStartedAt = null;
        var d = DateTime.Now - started;
        return d > TimeSpan.Zero ? d : TimeSpan.Zero;
    }

    /// <summary>解析 <c>payload_json</c>；损坏或为空时返回 null（退化为只有基本信息）。</summary>
    private static TaskPayload? ParseTaskPayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<TaskPayload>(json);
        }
        catch
        {
            return null;
        }
    }

    // ================= 任务操作（暂停/继续/删除/定位/复制） =================

    /// <summary>
    /// 暂停：与取消不同 —— 保留断点，状态标记为可恢复，重启后仍在列表里。
    /// </summary>
    private void PauseTask(object? param)
    {
        if (param is not DownloadTaskViewModel vm || vm.State != TaskState.Running) return;

        // 这里**不设** vm.State = Paused，而是交给 RunTaskAsync 的 catch 去设。
        //
        // 原因：引擎靠取消令牌停下来，从 Cancel() 到任务真正退出有几十~几百毫秒。
        // 如果这里先把状态改成 Paused，用户就立刻能点「继续」→ ResumeTask 会清掉
        // PauseRequested 并启动一轮新的 RunTaskAsync，而旧那轮的 catch 随后才执行，
        // 它看到 PauseRequested 已是 false，就会把状态覆盖成「已取消」。
        // 让状态只在 catch 里变，等于用「CanResume 的可见性」天然串行化了这两条路径。
        vm.PauseRequested = true;
        vm.Phase = "正在暂停…";
        vm.Cts?.Cancel();
        vm.Engine?.CancelAll();
        _ = LogAsync($"正在暂停：{vm.FileName}…");
    }

    /// <summary>继续：从断点恢复。</summary>
    private void ResumeTask(object? param)
    {
        if (param is not DownloadTaskViewModel vm) return;
        if (vm.State is not (TaskState.Paused or TaskState.Failed)) return;

        // 继续 = 撤销「取消/移除」意图，并清掉暂停标记。
        // 这三者都必须是显式字段：RunTaskAsync 靠它们判断「该不该跑」，
        // 而不是靠 Cts 的状态（旧的 Cts 可能已被取消且不可复用）。
        vm.Cancelled = false;
        vm.RemoveRequested = false;
        vm.PauseRequested = false;
        vm.Error = "";
        vm.State = TaskState.Queued;

        // 网盘任务续传要重新占住分享引用，否则这轮结束时会把计数多减一次
        if (vm.CloudContext != null && vm.TryHoldShareRef()) AddShareRef(vm.CloudContext);

        _ = LogAsync($"继续：{vm.FileName}");
        _ = RunTaskAsync(vm);
        RecalcTotals();
    }

    private void CancelTask(object? param)
    {
        if (param is not DownloadTaskViewModel vm || !vm.CanCancel) return;

        // ★ 必须先立「已取消」这个显式标志，再发 CTS 取消。
        //
        //   排队中的任务没有 CTS（要拿到并发槽位才创建），只发 Cancel 等于什么都没做：
        //   轮到它时 RunTaskAsync 拿不到任何"该停"的信号，照常开始下载。
        //   这就是「取消排队任务没反应」的根因。
        vm.Cancelled = true;
        vm.Cts?.Cancel();
        vm.Engine?.CancelAll();

        // 排队中的任务不会进 RunTaskAsync 的 catch，状态得在这里落定，
        // 否则会一直显示「等待中」。运行中的任务交给 catch 处理（它知道该写 Cancelled 还是 Paused）。
        if (vm.State == TaskState.Queued)
        {
            vm.State = TaskState.Cancelled;
            vm.Speed = 0;
            PersistTask(vm);
        }

        _ = LogAsync($"已请求取消：{vm.FileName}（可续传）");
    }

    private void CancelAll()
    {
        int n = 0;
        foreach (var t in Tasks)
        {
            if (!t.CanCancel) continue;
            t.Cancelled = true;
            t.Cts?.Cancel();
            t.Engine?.CancelAll();
            if (t.State == TaskState.Queued)
            {
                t.State = TaskState.Cancelled;
                t.Speed = 0;
                PersistTask(t);
            }
            n++;
        }
        _ = LogAsync(n > 0 ? $"已请求取消 {n} 个任务（可续传）" : "没有进行中的任务");
    }

    /// <summary>
    /// 按平台解析线程数：优先用「按平台线程数」里为该平台配的值，否则用全局设置。
    ///
    /// <para>这个设置项（<see cref="DownloadTuning.ThreadsFor"/>）原本存在但无人调用，
    /// 属于"假开关"。这里接上：网盘接口限流普遍比 GitHub 严，给不同平台配不同并发
    /// 是有实际意义的。</para>
    /// </summary>
    private int ResolveThreads(DownloadTaskViewModel vm)
    {
        int global = Math48.Clamp(_settings.Threads, 1, 32);
        string? platform = vm.Platform
            ?? (vm.Url.Contains("github", StringComparison.OrdinalIgnoreCase) ? "github" : null);

        if (string.IsNullOrEmpty(platform)) return global;

        var map = _settings.ThreadsPerPlatform;
        if (map != null && map.TryGetValue(platform, out int per))
            return Math48.Clamp(per, 1, 32);

        return global;
    }

    private void DeleteTask(object? param)
    {
        if (param is not DownloadTaskViewModel vm) return;

        // ★ 先确认，再动手。
        //
        //   删除任务是不可逆操作（成功完成的任务连文件都可能一起删），
        //   而「删除」按钮就在列表右侧，误点代价很高。
        //   完成的任务额外提供「连文件一起删」，因为那是用户最常想做的清理动作 ——
        //   但绝不能不问就删：文件可能已经被移到别处、或用户只想清列表。
        string? filePath = vm.ResultPath;
        bool fileExists = !string.IsNullOrEmpty(filePath) && File.Exists(filePath);

        if (fileExists)
        {
            int pick = Views.PromptDialog.ChooseWithPrimary(
                "删除下载记录",
                $"「{vm.FileName}」已下载完成，文件仍在：\n{filePath}\n\n要连文件一起删除吗？",
                1,   // 「记录和文件一起删除」是主操作（也最危险），用强调色让它显眼
                "仅删除记录", "记录和文件一起删除");

            if (pick < 0) return;                  // 取消
            if (pick == 1)
            {
                try
                {
                    File.Delete(filePath!);
                    _ = LogAsync($"已删除文件：{Path.GetFileName(filePath)}");
                }
                catch (Exception ex)
                {
                    // 删不掉（占用/权限）不该阻断删记录，但要明确告诉用户
                    _ = LogAsync($"无法删除文件：{ex.Message}", true);
                }
            }
        }
        else
        {
            int pick = Views.PromptDialog.Choose(
                "删除下载记录",
                $"确定要删除「{vm.FileName}」这条记录吗？",
                "删除");
            if (pick < 0) return;
        }

        // ★ 立「已移除」标志。
        //
        //   删除**排队中**的任务时，RunTaskAsync 可能正卡在 AcquireSlotAsync 里等待。
        //   只从 Tasks 列表移除是不够的：等它拿到槽位照样会开跑，
        //   于是出现「界面上已经没有这个任务，后台却在下文件」。
        //   标志必须在移除之前置位，且 RunTaskAsync 拿到槽位后要复查。
        vm.RemoveRequested = true;

        // 正在跑的要先停
        if (vm.CanCancel)
        {
            vm.Cancelled = true;
            vm.Cts?.Cancel();
            vm.Engine?.CancelAll();
        }

        // 排队中的任务不会进 catch，状态在这里落定，避免"删了但还显示等待中"的中间态
        if (vm.State == TaskState.Queued)
        {
            vm.State = TaskState.Cancelled;
            vm.Speed = 0;
        }

        // ★ 回收孤儿断点。
        //
        //   取消 / 失败时 part 会保留（续传需要），但只要用户把这条任务从列表里删掉，
        //   它就再没有任何入口能清理 —— 一个和文件同等大小的垃圾永久留在磁盘上。
        //   下载中删除时引擎可能还在写，先删会失败，所以放到后台稍后重试一次。
        //   成功完成的任务 part 已被搬走，File.Exists 为 false，此处自然跳过。
        string? part = vm.PartPath;
        if (!string.IsNullOrEmpty(part))
        {
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        if (!File.Exists(part)) break;
                        File.Delete(part);
                        // 元数据跟着一起清，否则留下一个指向不存在 part 的 .json
                        YunX.Core.Download.PartMetadata.Delete(part);
                        await LogAsync($"已清理未完成的断点文件：{Path.GetFileName(part)}")
                            .ConfigureAwait(false);
                        break;
                    }
                    catch (IOException)
                    {
                        // 引擎可能还持有写句柄，等它释放（每次 300ms，最多 3 秒）
                        await Task.Delay(300).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;   // 权限等其它问题：不打扰用户，忽略
                    }
                }
            });
        }

        Tasks.Remove(vm);
        // 按数据库主键删除。早期用的是 vm.TaskId（进程内编号），
        // 它和库里的自增主键根本不是一套，删除会打空。
        if (vm.DbId > 0) _services?.Tasks.Delete(vm.DbId);

        // 删任务是"真的不要了"：释放分享引用（归零会清理网盘上的转存临时文件）。
        // 暂停/取消路径故意不释放，所以这里必须补上，否则转存文件会一直留在用户网盘。
        try { ReleaseShareRef(vm); } catch { /* 善后失败不影响删除 */ }
        _services?.DownloadDebug.Forget(vm.TaskId);
        HasTasks = Tasks.Count > 0;
        RecalcTotals();
        _ = LogAsync($"已删除任务：{vm.FileName}");
    }

    /// <summary>打开文件（用系统默认程序）。</summary>
    private void OpenFile(object? param)
    {
        if (param is not DownloadTaskViewModel vm || vm.ResultPath is not { Length: > 0 } path)
        {
            _ = LogAsync("文件尚未下载完成", true);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path, UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            _ = LogAsync($"打开文件失败：{e.Message}", true);
        }
    }

    /// <summary>
    /// 在资源管理器中显示（<b>直接选中该文件</b>，不是只打开目录）。
    /// 这是原项目一个很实用的细节：下载了几十个文件时，"打开目录再自己找"很烦。
    /// </summary>
    private void RevealFile(object? param)
    {
        if (param is not DownloadTaskViewModel vm) return;
        string? path = vm.ResultPath;
        try
        {
            if (path is { Length: > 0 } && File.Exists(path))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else
            {
                OpenInExplorer(Path.GetDirectoryName(path) ?? _settings.DownloadDir);
            }
        }
        catch (Exception e)
        {
            _ = LogAsync($"定位文件失败：{e.Message}", true);
        }
    }

    /// <summary>复制直链。</summary>
    private void CopyLink(object? param)
    {
        string text = param switch
        {
            DownloadTaskViewModel vm => vm.Url,
            ResolvedItem item => item.DirectUrl ?? item.Locator,
            _ => InputText,
        };
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            _ = LogAsync("已复制链接到剪贴板");
        }
        catch (Exception e)
        {
            _ = LogAsync($"复制失败：{e.Message}", true);
        }
    }

    // ================= 收藏 =================

    private void ToggleFavorite(object? param)
    {
        string input = (param as string) is { Length: > 0 } s ? s : InputText.Trim();
        if (input.Length == 0) return;
        if (_services == null) return;

        if (_services.Favorites.Exists(input))
        {
            _services.Favorites.Remove(input);
            IsFavorited = false;
            _ = LogAsync("已取消收藏");
        }
        else
        {
            // ★ 标题来源要判断，不能直接用 ParsedTitle。
            //   ParsedTitle 的初始值是输入框的占位提示「等待输入链接」——
            //   用户没解析就直接收藏时，这个占位符会被当成标题存进库，
            //   侧边栏上就全是「等待输入链接」，完全看不出收藏的是什么。
            //   没解析过就留空 label，交给 FavoriteEntry.DisplayLabel 生成短名（owner/repo 之类）。
            string label = !string.IsNullOrWhiteSpace(ParsedTitle)
                           && HasParsed
                           && !ParsedTitle.StartsWith("等待", StringComparison.Ordinal)
                           && string.Equals(_resolvedFor, input, StringComparison.Ordinal)
                ? ParsedTitle
                : "";

            _services.Favorites.Add(input, label);
            IsFavorited = true;
            _ = LogAsync($"已收藏：{(label.Length > 0 ? label : input)}");
        }
        RefreshFavorites();
    }

    /// <summary>
    /// 给收藏起名字。
    ///
    /// <para><b>留空 = 恢复默认</b>：清掉自定义名后显示会退回网址，
    /// 这与用户"不然就显示网址"的要求一致。所以在提示里写明这一点，
    /// 而不是给一个单独的「清除」按钮 —— 少一个控件，语义也更直接。</para>
    /// </summary>
    private void RenameFavorite(object? param)
    {
        if (param is not FavoriteEntry f) return;
        if (_services == null) return;

        // 预填当前自定义名；如果还没起过名字，就用网址占位，方便用户直接改
        string initial = f.LabelCustom ? f.Label : "";

        try
        {
            string? name = Views.PromptDialog.Ask(
                "重命名收藏",
                "给这个收藏起个名字（留空则显示网址）：\n" + f.RawInput,
                secret: false,
                initial: initial);

            if (name == null) return;   // 取消

            if (_services.Favorites.Rename(f.RawInput, name))
            {
                RefreshFavorites();
                _ = LogAsync(string.IsNullOrWhiteSpace(name)
                    ? $"已清除收藏名称：{f.RawInput}"
                    : $"收藏已重命名：{name.Trim()}");
            }
            else
            {
                _ = LogAsync("重命名失败：这条收藏可能已被删除", true);
            }
        }
        catch (Exception ex)
        {
            _ = LogAsync($"重命名失败：{ex.Message}", true);
        }
    }

    /// <summary>删除单条收藏（收藏页的行内「删除」按钮）。</summary>
    private void RemoveFavorite(object? param)
    {
        if (param is not FavoriteEntry f) return;
        if (_services == null) return;

        // 删除类操作统一走确认，防止误点（收藏一旦删掉要重新粘链接才能恢复）
        if (Views.PromptDialog.Choose("取消收藏",
                $"确定要删除收藏「{f.DisplayLabel}」吗？", "删除") < 0)
            return;

        if (_services.Favorites.Remove(f.RawInput))
        {
            RefreshFavorites();
            _ = LogAsync($"已取消收藏：{f.DisplayLabel}");
        }
    }

    private void ClearAllFavorites()
    {
        if (_services == null) return;
        int n = Favorites.Count;
        if (n == 0) return;

        // 弹窗和清库都可能抛（Owner 设置、SQLite 写失败等）。
        // 不接住的话会一路冒到 DispatcherUnhandledException，
        // 用户看到的是「Argument Exception: 无法将 Owner 属性设置为它本身」这种
        // 纯技术报错 —— 既看不懂也没提示能做什么。
        try
        {
            var ok = Views.PromptDialog.Choose("清空收藏",
                $"确定要清空全部 {n} 条收藏吗？此操作不可撤销（不影响已下载的文件）。",
                "清空");
            // Choose 自己会补一个「取消」按钮，这里只传真正的选项，
            // 否则会出现「清空 / 取消 / 取消」三个按钮（曾经就这样）。
            // 返回 0 = 选了「清空」，-1 = 取消。
            if (ok != 0) return;

            _services.Favorites.Clear();
            RefreshFavorites();
            _ = LogAsync($"已清空 {n} 条收藏");
        }
        catch (Exception ex)
        {
            _ = LogAsync($"清空收藏失败：{ex.Message}", true);
        }
    }

    private void UseFavorite(object? param)
    {
        if (param is not FavoriteEntry f) return;        InputText = f.RawInput;
        _services?.Favorites.MarkUsed(f.RawInput);
        _ = LogAsync($"从收藏载入：{f.Label}");

        // 收藏在侧边栏，可能从任何页面点进来 —— 必须切回解析页，
        // 否则输入框填好了但用户看不见，等于没反应。
        Page = AppPage.Parse;

        // 顺带直接解析：从侧边栏点收藏的意图就是"我要下这个"，
        // 还让用户再点一次「解析」是多余的。
        if (ParseCommand.CanExecute(null)) ParseCommand.Execute(null);
    }

    private void RefreshFavorites()
    {
        if (_services == null) return;
        Favorites.Clear();
        foreach (var f in _services.Favorites.List()) Favorites.Add(f);

        // 空态依赖数量，必须手动通知（ObservableCollection 不会让计算属性失效）
        OnPropertyChanged(nameof(HasFavorites));
        // 列表可见性同时看"有没有收藏"和"是否收起"，两个都要重算
        OnPropertyChanged(nameof(FavoritesListVisible));
    }

    // ================= 历史 =================

    private void UseHistory(object? param)
    {
        if (param is not HistoryEntry h) return;
        // 原项目支持「复用提取码」：历史里记了提取码，载入时一并带上
        InputText = h.Passcode.Length > 0 ? $"{h.RawInput} 提取码:{h.Passcode}" : h.RawInput;
        _ = LogAsync($"从历史载入：{h.Title}");
    }

    private void DeleteHistory(object? param)
    {
        if (param is not HistoryEntry h) return;

        if (Views.PromptDialog.Choose("删除解析历史",
                $"确定要删除「{h.Title}」这条历史吗？", "删除") < 0)
            return;

        _services?.History.Delete(h.Id);
        RefreshHistory();
    }

    private void ClearHistory()
    {
        int count = History.Count;
        if (count == 0) return;

        // 批量删除风险更高：把条数写进文案里，让用户看清代价
        if (Views.PromptDialog.Choose("清空解析历史",
                $"确定要删除全部 {count} 条解析历史吗？此操作不可撤销。", "清空") < 0)
            return;

        _services?.History.Clear();
        RefreshHistory();
        _ = LogAsync("已清空解析历史");
    }

    private void RefreshHistory()
    {
        if (_services == null) return;
        History.Clear();
        foreach (var h in _services.History.Query(HistoryKeyword)) History.Add(h);

        // 空态与占位文案依赖数量和搜索词，必须手动通知
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HistoryEmptyText));
    }

    // ================= 日志导出 / 调优 / 更新 =================

    private void ExportLogs()
    {
        if (_services == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                string outDir = _settings.DownloadDir;
                string? zip = await _services.Diagnostics.ExportZipAsync(outDir)
                    .ConfigureAwait(false);
                if (zip != null)
                {
                    await LogAsync($"诊断日志已导出：{zip}");
                    OpenInExplorer(Path.GetDirectoryName(zip));
                }
                else
                {
                    await LogAsync("没有可导出的诊断日志（请先在设置里开启诊断模式）", true);
                }
            }
            catch (Exception e)
            {
                await LogAsync($"导出日志失败：{e.Message}", true);
            }
        });
    }

    /// <summary>一键重置实验性参数为默认值。</summary>
    private void ResetTuning()
    {
        _settings.ReadBufferKb = 96;
        _settings.PreemptFloorKb = 12;
        _settings.PreemptAgeSec = 15;
        _settings.EnableHttp2 = false;
        OnPropertyChanged(nameof(ReadBufferKb));
        OnPropertyChanged(nameof(PreemptFloorKb));
        OnPropertyChanged(nameof(PreemptAgeSec));
        OnPropertyChanged(nameof(EnableHttp2));
        _store.Save(_settings);
        _ = LogAsync("实验性参数已重置为默认值");
    }

    // ================= 网盘登录 / 备份 =================

    /// <summary>
    /// 网盘登录入口。
    /// 按平台支持的登录方式分流：内嵌浏览器 / 账号密码 / 手动粘贴。
    /// </summary>
    private async Task CloudLogoutAsync(object? param)
    {
        if (param is not CloudAccountRow row || _services == null) return;

        int pick = Views.PromptDialog.Choose(
            "登出 " + row.DisplayName,
            $"将删除本机保存的 {row.DisplayName} 登录凭证（Cookie）。\n\n" +
            "下次使用需要重新登录；已下载的文件不受影响。",
            "登出");
        if (pick != 0) return;

        try
        {
            _services.Credentials.Delete(row.Platform);
            row.IsLoggedIn = false;
            row.Status = "未登录";
            BuildCloudAccountRows();
            await LogAsync($"已登出：{row.DisplayName}（凭证已清除）").ConfigureAwait(true);
        }
        catch (Exception e)
        {
            await LogAsync($"登出失败：{e.Message}", true).ConfigureAwait(true);
        }
    }

    private async Task CloudLoginAsync(object? param)
    {
        if (param is not CloudAccountRow row || _services == null) return;

        try
        {
            switch (row.Login?.Kind)
            {
                case LoginKind.Browser:
                {
                    // 三条路让用户选：浏览器已登录过的话「导入」是零成本，
                    // 内嵌登录适合没装对应浏览器或想用独立会话的场景。
                    var domains = row.Login.CookieDomains;
                    int choice = Views.PromptDialog.Choose(
                        "登录 " + row.DisplayName,
                        $"选择获取 {row.DisplayName} 登录态的方式：\n\n" +
                        "· 从本机浏览器导入 —— 如果你已在 Chrome/Edge 登录过，这是最快的\n" +
                        "· 内嵌浏览器登录 —— 在应用内开一个窗口登录\n" +
                        "· 手动粘贴 —— 自己从开发者工具复制 Cookie",
                        "从本机浏览器导入（推荐）",
                        "内嵌浏览器登录",
                        "手动粘贴 Cookie");

                    if (choice == 0)
                    {
                        await LogAsync($"正在从浏览器读取 {row.DisplayName} 的 Cookie…");
                        var imported = await Task.Run(() =>
                            Core.Windows.BrowserCookieReader.Import(domains)).ConfigureAwait(true);

                        if (imported.Count > 0)
                        {
                            _services.SaveCredential(row.Platform, imported.CookieHeader);
                            row.IsLoggedIn = true;
                            row.Status = "已登录";
                            await LogAsync(
                                $"已从 {imported.Source} 导入 {imported.Count} 条 Cookie" +
                                (imported.Note != null ? $"（{imported.Note}）" : ""));
                        }
                        else
                        {
                            await LogAsync($"{row.DisplayName} 导入失败：{imported.Note}", true);
                            if (imported.AppBoundBlocked)
                            {
                                await LogAsync(
                                    "新版 Chrome/Edge 启用了 App-Bound 加密，第三方程序无法解密。" +
                                    "请改用「内嵌浏览器登录」或「手动粘贴」。", true);
                            }
                        }
                    }
                    else if (choice == 1)
                    {
                        var win = new Views.BrowserLoginWindow(
                            row.DisplayName,
                            row.Login.LoginUrl ?? "https://www.baidu.com",
                            domains);
                        bool? ok = win.ShowDialog();
                        if (ok == true && !string.IsNullOrEmpty(win.CapturedCookie))
                        {
                            _services.SaveCredential(row.Platform, win.CapturedCookie);
                            row.IsLoggedIn = true;
                            row.Status = "已登录";
                            await LogAsync($"{row.DisplayName} 登录成功");
                        }
                        else
                        {
                            await LogAsync($"{row.DisplayName} 登录未完成", true);
                        }
                    }
                    else if (choice == 2)
                    {
                        string? value = Views.PromptDialog.Ask("登录 " + row.DisplayName,
                            "粘贴 Cookie（整段 name=value; name2=value2 即可）");
                        if (string.IsNullOrWhiteSpace(value)) return;
                        _services.SaveCredential(row.Platform, value.Trim());
                        row.IsLoggedIn = true;
                        row.Status = "已登录";
                        await LogAsync($"{row.DisplayName} 凭证已保存");
                    }
                    else
                    {
                        return;
                    }

                    CloudStatus = $"已登录 {CloudAccounts.Count(x => x.IsLoggedIn)} / {CloudAccounts.Count}";
                    break;
                }

                case LoginKind.Password when row.Platform == Core.Cloud.CloudPlatforms.Pan123:
                {
                    var (account, password) = Views.PromptDialog.AskTwo("登录 " + row.DisplayName,
                        "账号", "密码", secret: true);
                    if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(password)) return;

                    if (_services.CloudDrives.Find(row.Platform) is
                        Core.Cloud.Drives.Pan123Drive pan123)
                    {
                        string token = await pan123.PasswordLoginAsync(account, password)
                            .ConfigureAwait(true);
                        _services.SaveCredential(row.Platform, token);
                        row.IsLoggedIn = true;
                        row.Status = "已登录";
                        await LogAsync($"{row.DisplayName} 登录成功");
                    }
                    break;
                }

                case LoginKind.ManualToken:
                case LoginKind.TokenOnly:
                case LoginKind.PasswordWithSms:
                default:
                {
                    // 兜底：手动粘贴 Cookie / Token
                    string? value = Views.PromptDialog.Ask("登录 " + row.DisplayName,
                        "粘贴 Cookie 或 Token（整段粘贴即可）");
                    if (string.IsNullOrWhiteSpace(value)) return;
                    _services.SaveCredential(row.Platform, value.Trim());
                    row.IsLoggedIn = true;
                    row.Status = "已登录";
                    await LogAsync($"{row.DisplayName} 凭证已保存");
                    break;
                }
            }

            CloudStatus = $"已登录 {CloudAccounts.Count(x => x.IsLoggedIn)} / {CloudAccounts.Count}";
        }
        catch (Exception e)
        {
            await LogAsync($"{row.DisplayName} 登录失败：{e.Message}", true);
        }
    }

    /// <summary>
    /// 导出认证备份（.yunx）。
    ///
    /// <para>用<b>用户口令</b>派生的密钥（PBKDF2 + AES-GCM）加密全部凭证，
    /// 与主密钥体系独立 —— 这样备份可以跨设备恢复。</para>
    /// </summary>
    private async Task ExportBackupAsync()
    {
        if (_services == null) return;
        try
        {
            string? pwd = Views.PromptDialog.Ask("导出认证备份",
                "设置一个备份口令（用于在别的设备恢复）", secret: true);
            if (string.IsNullOrEmpty(pwd) || pwd.Length < 6)
            {
                await LogAsync("口令至少 6 位", true);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存认证备份",
                Filter = "YunX 备份 (*.yunx)|*.yunx",
                FileName = $"yunx_auth_{DateTime.Now:yyyyMMdd}.yunx",
            };
            if (dlg.ShowDialog() != true) return;

            string path = await Task.Run(() =>
                Core.Data.AuthBackup.Export(_services.Database, _services.Cipher, pwd, dlg.FileName))
                .ConfigureAwait(true);

            await LogAsync($"备份已导出：{path}");
            OpenInExplorer(Path.GetDirectoryName(path));
        }
        catch (Exception e)
        {
            await LogAsync($"导出备份失败：{e.Message}", true);
        }
    }

    /// <summary>导入认证备份。</summary>
    private async Task ImportBackupAsync()
    {
        if (_services == null) return;
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择认证备份",
                Filter = "YunX 备份 (*.yunx)|*.yunx",
            };
            if (dlg.ShowDialog() != true) return;

            string? pwd = Views.PromptDialog.Ask("导入认证备份",
                "输入导出时设置的备份口令", secret: true);
            if (string.IsNullOrEmpty(pwd)) return;

            int n = await Task.Run(() =>
                Core.Data.AuthBackup.Import(_services.Database, _services.Cipher, pwd, dlg.FileName))
                .ConfigureAwait(true);

            BuildCloudAccountRows();
            await LogAsync($"已恢复 {n} 个平台的凭证");
        }
        catch (Exception e)
        {
            await LogAsync($"导入备份失败：{e.Message}", true);
        }
    }

    /// <summary>打开网盘账号页（导航）。</summary>
    private void OpenFolderNav(object? param) => Navigate(param);

    /// <summary>填入示例链接，降低首次使用门槛。</summary>
    private void UseSample(object? param)
    {
        InputText = (param as string)?.ToLowerInvariant() switch
        {
            "release" => "https://github.com/yt-dlp/yt-dlp/releases",
            "file" => "https://github.com/octocat/Hello-World/blob/master/README",
            _ => "https://github.com/yt-dlp/yt-dlp",
        };
    }

    private void ClearFinished()
    {
        foreach (var t in Tasks.Where(x => x.IsFinished).ToList())
            Tasks.Remove(t);
        HasTasks = Tasks.Count > 0;
        RecalcTotals();
    }

    private void SetAllAssets(bool selected)
    {
        foreach (var a in Assets) a.IsSelected = selected;
    }

    private void SaveSettingsFromUi()
    {
        _settings.DownloadDir = DownloadDir;
        _store.Save(_settings);

        // 应用需要重建的项：联网模式 / 线路选择 / DoH
        _ = ApplySettingsAsync();
    }

    private async Task ApplySettingsAsync()
    {
        await LogAsync("应用设置…");

        if (!_settings.BypassHosts) HostsBypass.Disable();
        else await HostsBypass.EnableAsync(m => LogAsync(m)).ConfigureAwait(true);

        await _provider.SetModeAsync(_settings.NetworkMode, m => LogAsync(m))
            .ConfigureAwait(true);
        NetModeText = "联网模式：" + _provider.Describe();

        RebuildNetworkStack();
        RefreshMirrorOptions();
        await LogAsync("设置已保存");
    }

    // ===================== 汇总与日志 =====================

    /// <summary>
    /// 重算汇总。枚举 <see cref="Tasks"/>（ObservableCollection），因此**必须在 UI 线程**执行。
    ///
    /// <para>调用方已经尽力保证线程亲和（<see cref="RunTaskAsync"/> 全程 <c>ConfigureAwait(true)</c>），
    /// 这里再做一次兜底：属性赋值会触发 <c>PropertyChanged</c>，进而驱动视图与任务栏进度，
    /// 一旦落到后台线程就是难查的偶发异常。与其依赖调用方自觉，不如自己守住。</para>
    /// </summary>
    private void RecalcTotals()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            // BeginInvoke 而非 Invoke：调用方可能正持有锁，同步等待会死锁
            dispatcher.BeginInvoke(new Action(RecalcTotals));
            return;
        }

        RefreshDuplicateNames();

        var active = Tasks.Where(t => t.IsRunning).ToList();
        RunningCount = active.Count(t => t.State == TaskState.Running);
        long totalSize = Tasks.Where(t => t.Size > 0).Sum(t => t.Size);
        long totalDone = Tasks.Sum(t => Math.Min(t.Done, t.Size > 0 ? t.Size : t.Done));

        TotalPercent = totalSize > 0 ? Math.Min(100.0, totalDone * 100.0 / totalSize) : 0;

        if (active.Count > 0)
        {
            double speed = active.Sum(t => t.Speed);
            double eta = speed > 1 && totalSize > 0 ? (totalSize - totalDone) / speed : 0;
            StatusText = $"下载中 {active.Count} 个 · " +
                         $"{Humanize.Size(totalDone)} / {Humanize.Size(totalSize)}";
            TotalSpeedText = $"{Humanize.Speed(speed)} · 剩余 {Humanize.Eta(eta)}";
        }
        else
        {
            int done = Tasks.Count(t => t.State == TaskState.Done);
            StatusText = Tasks.Count == 0 ? "就绪" : $"已完成 {done} / {Tasks.Count} 个";
            TotalSpeedText = "";
        }
    }

    /// <summary>
    /// 标记"名字有重复"的任务，让它们显示保存目录。
    ///
    /// <para><b>为什么需要</b>：文件名相同不代表同一个文件。下到不同目录的两个
    /// <c>rg.zip</c> 在列表里完全一样 —— 除非点「定位」跳到别处，用户根本发现不了。
    /// 只在真有重名时才附带目录，否则每条都挂一长串路径，界面会很吵。</para>
    /// </summary>
    private void RefreshDuplicateNames()
    {
        // 统计每个名字出现几次；只有 >1 的才需要额外信息区分
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tasks)
        {
            if (counts.TryGetValue(t.FileName, out int n)) counts[t.FileName] = n + 1;
            else counts[t.FileName] = 1;
        }

        foreach (var t in Tasks)
            t.ShowDir = counts.TryGetValue(t.FileName, out int c) && c > 1;
    }

    private Task LogAsync(string message, bool isError = false)
    {
        // 退出路径上服务已经释放（SQLite / 诊断日志 / 连接池都关了）。
        // RunTaskAsync 的 catch 仍可能在这之后写一行日志，这里直接丢弃 ——
        // 输出调试信息不该在关闭阶段抛异常。字段用 volatile 读取，避免读到半释放的对象。
        if (_disposed) return Task.CompletedTask;

        string line = $"[{DateTime.Now:HH:mm:ss}] {(isError ? "! " : "")}{message}";
        Debug.WriteLine(line);

        _ui.Post(_ =>
        {
            LogLines.Add(line);
            // 只保留最近 300 行，避免长时间运行内存膨胀
            while (LogLines.Count > 300) LogLines.RemoveAt(0);
        }, null);
        return Task.CompletedTask;
    }

    /// <summary>窗口关闭时取消所有任务。</summary>
    /// <summary>
    /// 退出前的收尾。
    ///
    /// <para>早期实现只「取消任务 + 存设置」，而 <see cref="AppServices.Dispose"/> 虽然写好了
    /// 却<b>从来没有调用点</b> —— 后果是 SQLite 的 <c>wal_checkpoint(TRUNCATE)</c> 永不执行
    /// （<c>yunx.db-wal</c> 一直涨，靠下次启动删文件自愈）、诊断日志队列里积压的行全丢、
    /// 共享连接池不释放。</para>
    /// </summary>
    public void Shutdown()
    {
        if (_disposed) return;

        try
        {
            // 先停定时器：否则它们还会在收尾期间触发回调去访问已释放的服务
            _clipTimer?.Stop();
            _debugTimer?.Stop();

            foreach (var t in Tasks.Where(x => x.CanCancel))
            {
                t.Cts?.Cancel();
                t.Engine?.CancelAll();
            }

            // ★ 给在飞任务一个**有界**的收尾窗口，再释放服务。
            //
            //   调用关系是 MainWindow.OnClosing → Shutdown() → Dispose()，而 Dispose
            //   会立刻关掉 SQLite 与共享连接池。上一步的 Cancel 只是**请求**取消：
            //   引擎还要走 catch(OperationCanceledException) → PersistMeta(force:true)
            //   → finally 里 FlushToDisk，然后回到 RunTaskAsync 的 catch 里写日志
            //   （Diagnostics → 已释放的库）与 RecalcTotals。这段代码在释放之后才跑，
            //   就会在退出路径上抛 ObjectDisposedException / SQLite 已关闭。
            //   它们是兜底 catch 能吞掉的，但日志会丢、断点也可能没保住。
            //   等待上限 3 秒：用户点关闭时不能无限期挂着。
            WaitForRunningTasks(TimeSpan.FromSeconds(3));

            // ★ 退出时把所有"可续传"的任务落盘，并**释放**它们占着的分享引用。
            //
            //   两个都需要：
            //   · 不落盘 → 重启后列表里没有这些任务，用户以为丢了；
            //   · 不释放 → 引用计数一直挂在内存里（进程销毁后对象没了，但用户网盘上的
            //     转存文件还在），下次登录会看到一堆临时文件。
            //   这里显式把任务标成 Paused 再落盘：重启后恢复为「可点继续」，
            //   而不是被当成 Running 后又卡在等待中。
            foreach (var t in Tasks.ToList())
            {
                if (t.State is TaskState.Running or TaskState.Queued)
                    t.State = TaskState.Paused;

                // Done 也要写：它们现在是"下载历史"，退出时补一次确保
                // 完成时间戳是最新的（PruneCompleted 按 updated_at 排序）。
                // 已经写过且状态没变的，这里多一次 UPDATE 无害。
                PersistTask(t);

                // 退出是"真的结束了这一轮"：释放分享引用（会触发转存清理）
                try { ReleaseShareRef(t); } catch { /* 善后失败不影响退出 */ }
            }

            // 退出时再裁一次：本次会话可能下了一堆，N 的配额要在关程序时生效
            PruneCompletedHistory();

            // 磁力引擎是独立子进程，必须显式停：否则它会作为孤儿进程继续跑，
            // 下次启动还占着 bolt 存储锁。
            try { _gopeedEngine?.Dispose(); _gopeedEngine = null; } catch { }

            _store.Save(_settings);
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>
    /// 等待所有在飞任务收尾（有界）。超时就直接返回 —— 取消信号已经发出，
    /// 进程退出时句柄由 OS 回收，断点元数据最坏丢 1 秒进度。
    ///
    /// <para><b>为什么不能直接 <c>Wait</c> / <c>Thread.Sleep</c></b>：<see cref="RunTaskAsync"/>
    /// 全程 <c>ConfigureAwait(true)</c>，它取消后的 catch/finally 需要**回到 UI 线程**才能继续。
    /// 在 UI 线程上阻塞等待，续体永远排不上队 → 必然等到超时，等于没等。
    /// 必须一边等一边推进 Dispatcher 队列（<see cref="System.Windows.Threading.DispatcherFrame"/>）。</para>
    /// </summary>
    private void WaitForRunningTasks(TimeSpan timeout)
    {
        Task[] snapshot;
        lock (_running) snapshot = _running.ToArray();
        if (snapshot.Length == 0) return;

        try
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                bool allDone = true;
                foreach (var t in snapshot)
                {
                    // 只等「已启动且未完成」的；已取消未启动的直接跳过
                    if (!t.IsCompleted) { allDone = false; break; }
                }
                if (allDone) return;

                // 跑一轮消息泵：让挂起的续体（Cancel 之后的 catch/finally）得以执行。
                // Background 优先级低于渲染与输入，不会让界面表现为「卡住不重绘」。
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
        }
        catch
        {
            // 收尾等待失败不影响退出
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 诊断日志必须先 flush 再释放数据库 ——
        // 数据库关闭过程中的异常也要能落进日志里
        try { _services?.Dispose(); } catch { /* 退出路径，忽略 */ }
        _services = null;

        GC.SuppressFinalize(this);
    }

    private volatile bool _disposed;
}

/// <summary>设置页的种子色色块。</summary>
public sealed class SeedSwatch
{
    public SeedSwatch(string hex, string name)
    {
        Hex = hex;
        Name = name;
    }

    public string Hex { get; }
    public string Name { get; }

    /// <summary>色块填充（供 XAML 绑定）。</summary>
    public System.Windows.Media.Brush Fill
    {
        get
        {
            var c = Themes.SeedPalette.Parse(Hex);
            var b = new System.Windows.Media.SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
