using YunX.Core.Cloud;
using YunX.Core.Download;
using YunX.Core.Mirrors;
using YunX.Core.Util;

namespace YunX.Core.Cloud;

/// <summary>下载来源类型。</summary>
public enum SourceKind
{
    GitHub,
    CloudDrive,
}

/// <summary>
/// 一次「从分享链接到文件落盘」的解析结果。
/// 这是 UI 与引擎之间的统一契约：无论 GitHub 还是网盘，最终都产出这个结构。
/// </summary>
public sealed class SourceResolution
{
    public SourceKind Kind { get; init; }
    /// <summary>来源标识（github / quark / baidu…）。</summary>
    public string Platform { get; init; } = "";
    /// <summary>显示标题。</summary>
    public string Title { get; init; } = "";
    public string Note { get; init; } = "";
    /// <summary>可直接下载的条目。<b>CloudDrive 类型可能是目录</b>，需要先展开。</summary>
    public List<ResolvedItem> Items { get; init; } = new();
    /// <summary>网盘分享上下文（GitHub 为 null）。</summary>
    public CloudShareContext? CloudContext { get; init; }
    /// <summary>当前目录（网盘浏览时为非空）。</summary>
    public string? CurrentDirectoryId { get; init; }
    /// <summary>下一页游标（网盘目录分页）。</summary>
    public string? NextCursor { get; init; }
    public bool HasMore => !string.IsNullOrEmpty(NextCursor);
    public bool IsMulti => Items.Count > 1;
}

/// <summary>一个可下载条目。</summary>
public sealed class ResolvedItem
{
    public required string Name { get; init; }
    /// <summary>GitHub：直链；网盘：文件 ID。</summary>
    public required string Locator { get; init; }
    public long Size { get; init; }
    public bool IsDirectory { get; init; }
    /// <summary>网盘文件的平台令牌。</summary>
    public string Token { get; init; } = "";
    /// <summary>已就绪的直链（GitHub 可直接下；网盘为空，需再取链）。</summary>
    public string? DirectUrl { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
}

/// <summary>网盘分享上下文（列目录、取链、清理都需要）。</summary>
public sealed class CloudShareContext
{
    public required ICloudDrive Drive { get; init; }
    public required ShareLink Share { get; init; }
}

/// <summary>
/// 统一来源解析器：识别输入属于 GitHub 还是哪个网盘，并给出可下载条目。
///
/// <para><b>为什么需要这一层</b>：原项目把「分享链接解析」和「GitHub 解析」写成两条独立路径，
/// UI 层要判断类型再分别调用。这里收敛成一个入口 —— UI 只管「给我这个输入能下什么」，
/// 不用关心平台差异。</para>
/// </summary>
public sealed class SourceResolver
{
    private readonly CloudRegistry _cloud;

    /// <summary>中文口令兜底解析器（星海）。为 null 时跳过口令兜底。</summary>
    private readonly StarSeaCommandResolver? _starSea;

    public Action<string>? Log { get; set; }

    public SourceResolver(CloudRegistry cloud, StarSeaCommandResolver? starSea = null)
    {
        _cloud = cloud;
        _starSea = starSea;
    }

    /// <summary>
    /// 解析输入。返回 null 表示无法识别。
    /// </summary>
    /// <param name="input">用户输入（可以是整段文本、中文口令）。</param>
    /// <param name="githubResolve">
    /// GitHub 解析委托（委托形式避免 Core 依赖 GitHub 项目）。
    /// 参数：输入 → 结果；由上层注入。
    /// </param>
    public async Task<SourceResolution?> ResolveAsync(string input,
        Func<string, Task<SourceResolution?>>? githubResolve = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        string trimmed = input.Trim();
        bool looksLikeCommand = ShareTextParser.LooksLikeChineseCommand(trimmed);

        // 1) 网盘优先：网盘链接特征比 GitHub 更明确
        var drive = _cloud.Detect(trimmed);

        // 1a) 中文口令：迅雷能直接把口令换回「分享 + 提取码」，最精确，因此优先。
        //     注意这里**不能 return null**：迅雷失败还要交给星海兜底（夸克/UC 的资源口令）。
        if (looksLikeCommand && drive is Drives.XunleiDrive xl)
        {
            Log?.Invoke("检测到中文口令，正在换取分享链接…");
            var kouling = await xl.ResolveKoulingAsync(trimmed, ct).ConfigureAwait(false);
            if (kouling != null)
            {
                Log?.Invoke($"口令已换取分享：{kouling.ShareId}");
                return await BrowseCloudAsync(xl, kouling, null, null, ct).ConfigureAwait(false);
            }
            Log?.Invoke("迅雷口令未匹配到分享，改用星海口令解析…");
        }
        else if (drive != null && !looksLikeCommand)
        {
            var share = drive.ParseShareLink(trimmed);
            if (share != null)
            {
                Log?.Invoke($"识别为 {drive.DisplayName} 分享");
                return await BrowseCloudAsync(drive, share, null, null, ct).ConfigureAwait(false);
            }
        }

        // 1b) 中文口令兜底：星海只把口令换成一个 pwdId，再合成夸克/UC 链接，
        //     因此拿到结果后要重新走一遍「识别 → 浏览」，不能直接当结果用。
        if (looksLikeCommand && _starSea != null)
        {
            Log?.Invoke("尝试口令解析（星海）…");
            var synthesized = await _starSea.ResolveAsync(trimmed, Log, ct).ConfigureAwait(false);
            if (synthesized != null)
            {
                var target = _cloud.Find(synthesized.Platform);
                if (target != null)
                {
                    return await BrowseCloudAsync(target, synthesized, null, null, ct)
                        .ConfigureAwait(false);
                }
            }
        }

        // 2) GitHub
        if (githubResolve != null)
        {
            var gh = await githubResolve(trimmed).ConfigureAwait(false);
            if (gh != null) return gh;
        }

        return null;
    }

    /// <summary>浏览网盘目录（根目录或子目录）。</summary>
    public async Task<SourceResolution> BrowseCloudAsync(ICloudDrive drive, ShareLink share,
        string? parentId, string? cursor, CancellationToken ct = default)
    {
        var listing = await drive.ListAsync(share, parentId, cursor, ct).ConfigureAwait(false);

        var result = new SourceResolution
        {
            Kind = SourceKind.CloudDrive,
            Platform = drive.Platform,
            Title = listing.Title.Length > 0 ? listing.Title : $"{drive.DisplayName} 分享",
            Note = $"{listing.Files.Count} 项",
            CloudContext = new CloudShareContext { Drive = drive, Share = share },
            CurrentDirectoryId = parentId,
            NextCursor = listing.NextCursor,
        };

        foreach (var f in listing.Files)
        {
            result.Items.Add(new ResolvedItem
            {
                Name = f.Name,
                Locator = f.Id,
                Size = f.Size,
                IsDirectory = f.IsDirectory,
                Token = f.Token,
            });
        }
        return result;
    }

    /// <summary>
    /// 把网盘条目换成直链。
    /// </summary>
    public async Task<DirectLink> GetCloudLinkAsync(CloudShareContext ctx, ResolvedItem item,
        CancellationToken ct = default)
    {
        var file = new CloudFile
        {
            Id = item.Locator,
            Name = item.Name,
            Size = item.Size,
            IsDirectory = item.IsDirectory,
            Token = item.Token,
        };
        return await ctx.Drive.GetDirectLinkAsync(ctx.Share, file, ct).ConfigureAwait(false);
    }
}
