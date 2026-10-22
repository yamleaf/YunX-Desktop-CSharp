namespace YunX.Core.Cloud;

/// <summary>网盘平台标识。</summary>
public static class CloudPlatforms
{
    public const string GitHub = "github";
    public const string Quark = "quark";
    public const string UC = "uc";
    public const string Xunlei = "xunlei";
    public const string Baidu = "baidu";
    public const string C139 = "c139";
    public const string Pan123 = "pan123";
    public const string Lanzou = "lanzou";
    public const string Pan115 = "pan115";
    public const string GuangYa = "guangya";
}

/// <summary>分享链接的识别结果。</summary>
public sealed class ShareLink
{
    public required string Platform { get; init; }
    /// <summary>分享 ID（各平台语义不同：夸克是 pwdId，123 是 shareKey…）。</summary>
    public required string ShareId { get; init; }
    /// <summary>从整段文本中提取到的提取码（可能为空）。</summary>
    public string? Passcode { get; init; }
    public string RawInput { get; init; } = "";
}

/// <summary>网盘中的一个文件/目录条目（对应原项目 ShareFile）。</summary>
public sealed class CloudFile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public long Size { get; init; }
    public bool IsDirectory { get; init; }
    /// <summary>父目录 ID。</summary>
    public string ParentId { get; init; } = "";
    /// <summary>
    /// 平台特有的令牌（如 123 的 fid_token、夸克的 share_fid_token）。
    /// 取直链时往往需要原样带回，故不解析其内部结构。
    /// </summary>
    public string Token { get; init; } = "";
    public string ModifyTime { get; init; } = "";

    public string SizeText => IsDirectory ? "" : Util.Humanize.Size(Size);
}

/// <summary>目录列表结果。</summary>
public sealed class CloudListing
{
    public List<CloudFile> Files { get; init; } = new();
    /// <summary>下一页游标；空表示没有更多。列表构建过程中需要赋值，故用 set。</summary>
    public string? NextCursor { get; set; }
    /// <summary>分享标题 / 根目录名。</summary>
    public string Title { get; init; } = "";
}

/// <summary>取到的下载直链。</summary>
public sealed class DirectLink
{
    public required string Url { get; init; }
    /// <summary>下载该链接必须携带的请求头（Referer / Cookie / UA）。</summary>
    public Dictionary<string, string> Headers { get; init; } = new();
    /// <summary>文件名（若平台给出）。</summary>
    public string? FileName { get; init; }
    public long Size { get; init; }
}

/// <summary>登录方式。</summary>
public enum LoginKind
{
    /// <summary>内嵌浏览器登录后自动抓 Cookie。</summary>
    Browser,
    /// <summary>账号 + 密码。</summary>
    Password,
    /// <summary>账号 + 密码 + 短信验证码（分两步）。</summary>
    PasswordWithSms,
    /// <summary>手动粘贴 Cookie / Token。</summary>
    ManualToken,
    /// <summary>只需 Token（如 GitHub PAT）。</summary>
    TokenOnly,
}

/// <summary>网盘的登录能力描述。</summary>
public sealed class LoginDescriptor
{
    public required LoginKind Kind { get; init; }
    /// <summary>内嵌登录要打开的地址。</summary>
    public string? LoginUrl { get; init; }
    /// <summary>登录成功后应从哪些域提取 Cookie。</summary>
    public string[] CookieDomains { get; init; } = Array.Empty<string>();
    /// <summary>给用户看的提示。</summary>
    public string Hint { get; init; } = "";
}

/// <summary>平台能力异常（如未登录、提取码错误）。</summary>
public class CloudException : Exception
{
    public CloudException(string message) : base(message) { }
}

/// <summary>未登录。</summary>
public sealed class NotLoggedInException : CloudException
{
    public NotLoggedInException(string platform)
        : base($"{platform} 未登录，请先在「账号」页登录") { }
}

/// <summary>提取码错误或缺失。</summary>
public sealed class PasscodeRequiredException : CloudException
{
    public PasscodeRequiredException(string message = "需要提取码") : base(message) { }
}

/// <summary>
/// 网盘适配器接口。
///
/// <para><b>移植说明</b>：原项目把每个网盘的逻辑写成 700~1000 行的独立类，
/// 但对外暴露的能力高度一致。这里抽成接口，让「解析 → 浏览 → 取直链 → 下载 → 清理」
/// 这条主链路与具体平台解耦，新增网盘只需实现本接口。</para>
///
/// <para><b>为什么要区分 Match / Resolve / List / Link 四步</b>：
/// 分享链接往往需要「提取码 → 验证 → 会话态」逐步推进，
/// 中间任何一步失败都要给用户精确提示（缺码 / 码错 / 过期 / 未登录），
/// 合并成一个方法会让错误处理无从下手。</para>
/// </summary>
public interface ICloudDrive
{
    /// <summary>平台标识（与 <see cref="CloudPlatforms"/> 对应）。</summary>
    string Platform { get; }

    /// <summary>平台显示名。</summary>
    string DisplayName { get; }

    /// <summary>登录方式描述；null 表示不需要登录。</summary>
    LoginDescriptor? Login { get; }

    /// <summary>能否识别这个输入（分享链接 / 中文口令等）。</summary>
    bool CanHandle(string input);

    /// <summary>
    /// 从输入中提取分享 ID 与提取码。
    /// 输入可能是整段文本（如「链接：xxx 提取码：abcd」）。
    /// </summary>
    ShareLink? ParseShareLink(string input);

    /// <summary>
    /// 列出分享根目录或指定目录的内容。
    /// </summary>
    /// <param name="share">分享信息。</param>
    /// <param name="parentId">父目录 ID；null 表示根目录。</param>
    /// <param name="cursor">分页游标。</param>
    Task<CloudListing> ListAsync(ShareLink share, string? parentId, string? cursor,
        CancellationToken ct = default);

    /// <summary>取某个文件的下载直链（含必需请求头）。</summary>
    Task<DirectLink> GetDirectLinkAsync(ShareLink share, CloudFile file,
        CancellationToken ct = default);

    /// <summary>
    /// 清理为该次下载产生的临时转存文件（百度/迅雷取链需要先转存）。
    /// 不需要转存的平台返回 <see cref="Task.CompletedTask"/> 即可。
    /// </summary>
    Task CleanupAsync(ShareLink share, CancellationToken ct = default);
}

/// <summary>
/// 网盘注册表：按顺序尝试各适配器识别输入。
///
/// <para>顺序有意义：识别规则宽的平台要排在后面，
/// 否则会把别的平台的链接也吞掉。</para>
/// </summary>
public sealed class CloudRegistry
{
    private readonly List<ICloudDrive> _drives = new();

    public IReadOnlyList<ICloudDrive> All => _drives;

    public void Register(ICloudDrive drive) => _drives.Add(drive);

    public ICloudDrive? Find(string platform) =>
        _drives.FirstOrDefault(d => d.Platform == platform);

    /// <summary>找到能处理该输入的网盘；都不匹配返回 null。</summary>
    public ICloudDrive? Detect(string input) =>
        _drives.FirstOrDefault(d => d.CanHandle(input));

    /// <summary>已注册的平台显示名（设置页/账号页用）。</summary>
    public IEnumerable<(string Platform, string Name, LoginDescriptor? Login)> Describe() =>
        _drives.Select(d => (d.Platform, d.DisplayName, d.Login));
}
